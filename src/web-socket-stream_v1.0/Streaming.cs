using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WsStream;

/// <summary>What ffmpeg will do for one play request.</summary>
internal sealed record StreamPlan(
    string Input,
    MediaStream? Video,
    MediaStream? Audio,
    bool HasVideo,
    bool VideoCopy,
    bool AudioCopy,
    string Mime,
    double? Duration,
    object Description);

/// <summary>
/// Decides copy vs transcode per stream from the browser's MediaSource capabilities
/// (sent as keys like "h264:high", "hevc:main10", "flac"), and builds the ffmpeg command.
/// Everything is at least remuxed: MSE only accepts fragmented MP4/WebM, not MKV/AVI/raw FLAC.
/// </summary>
internal static class StreamPlanner
{
    // Transcode target: H.264 High@4.1 8-bit + AAC-LC stereo — the one combo every MSE browser plays.
    private const string TranscodeVideoCodec = "avc1.640029";
    private const string TranscodeAudioCodec = "mp4a.40.2";

    public static StreamPlan? Plan(BaseItem item, MediaSourceInfo source, IReadOnlySet<string> caps, bool force, User user, out string? error)
    {
        error = null;
        var streams = source.MediaStreams ?? [];
        var isVideoItem = item.MediaType == MediaType.Video;

        var video = isVideoItem ? streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal) : null;
        var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && !s.IsExternal && s.Index == source.DefaultAudioStreamIndex)
                    ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && !s.IsExternal);
        var unprobed = streams.Count == 0; // e.g. .strm: nothing to decide on, so transcode blind
        var hasVideo = isVideoItem && (video is not null || unprobed);

        var canRemux = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        var vInfo = video is null ? null : VideoCodec(video);
        var aInfo = audio is null ? null : AudioCodec(audio);

        var vCopy = hasVideo && !force && canRemux && vInfo is not null && caps.Contains(vInfo.Value.Key) && !video!.IsInterlaced;
        var aCopy = !force && canRemux && aInfo is not null && caps.Contains(aInfo.Value.Key);

        if (hasVideo && !vCopy && !user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding))
        {
            error = $"This browser can't play {Label(video)} as-is, and your Jellyfin account isn't allowed to transcode video.";
            return null;
        }

        if ((audio is not null || unprobed) && !aCopy && !user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding))
        {
            error = $"This browser can't play {Label(audio)} as-is, and your Jellyfin account isn't allowed to transcode audio.";
            return null;
        }

        var hasAudio = audio is not null || unprobed;
        var vCodec = vCopy ? vInfo!.Value.Codec : TranscodeVideoCodec;
        var aCodec = aCopy ? aInfo!.Value.Codec : TranscodeAudioCodec;
        var codecs = hasVideo ? (hasAudio ? $"{vCodec},{aCodec}" : vCodec) : aCodec;
        var mime = $"{(hasVideo ? "video" : "audio")}/mp4; codecs=\"{codecs}\"";

        var input = source.Protocol == MediaProtocol.File ? "file:" + source.Path : source.Path;
        var ticks = source.RunTimeTicks ?? item.RunTimeTicks;
        var duration = ticks is long t && t > 0 ? t / 10_000_000d : (double?)null;

        var description = new
        {
            v = hasVideo ? new { src = Label(video), mode = vCopy ? "copy" : "transcode", @out = vCopy ? null : "H.264 ≤1080p" } : null,
            a = hasAudio ? new { src = Label(audio), mode = aCopy ? "copy" : "transcode", @out = aCopy ? null : "AAC stereo" } : null,
        };

        return new StreamPlan(input, video, audio, hasVideo, vCopy, aCopy, mime, duration, description);
    }

    public static List<string> Arguments(StreamPlan p, double start)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error" };
        if (start > 0.05)
        {
            a.Add("-ss");
            a.Add(start.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // -copyts keeps source timestamps, so after a seek the fragments land at their real position on the
        // MSE timeline; no timestampOffset guesswork, and copied video that starts at the previous keyframe stays correct.
        a.AddRange(["-copyts", "-i", p.Input]);

        if (p.HasVideo)
        {
            a.AddRange(["-map", p.Video is null ? "0:v:0" : $"0:{p.Video.Index}"]);
        }

        a.AddRange(["-map", p.Audio is null ? "0:a:0?" : $"0:{p.Audio.Index}"]);
        a.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        if (p.HasVideo && p.VideoCopy)
        {
            a.AddRange(["-c:v", "copy"]);
            if (string.Equals(p.Video?.Codec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                a.AddRange(["-tag:v", "hvc1"]); // ffmpeg defaults to hev1; Safari and some Chrome builds refuse it
            }
        }
        else if (p.HasVideo)
        {
            var filters = p.Video?.IsInterlaced == true ? "yadif," : string.Empty;
            // CPU cost lives here. For hardware encoding swap libx264 for h264_vaapi / h264_qsv / h264_nvenc
            // (and add the matching -hwaccel/-vf upload flags) — this does not read Jellyfin's HW accel settings.
            a.AddRange(
            [
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-maxrate", "12M", "-bufsize", "24M",
                "-profile:v", "high", "-level:v", "4.1", "-pix_fmt", "yuv420p",
                "-vf", filters + "scale=w='min(1920,iw)':h=-2",
                "-force_key_frames", "expr:gte(t,n_forced*2)",
            ]);
        }

        if (p.AudioCopy)
        {
            a.AddRange(["-c:a", "copy"]);
        }
        else
        {
            a.AddRange(["-c:a", "aac", "-b:a", p.HasVideo ? "192k" : "256k", "-ac", "2"]);
            if (p.Audio?.SampleRate > 48000)
            {
                a.AddRange(["-ar", "48000"]);
            }
        }

        // Fragmented MP4 that MSE can consume from a pipe: init segment first, then ~2 s fragments.
        //  - frag_discont + use_editlist 0 + avoid_negative_ts make_non_negative: tfdt carries the real source
        //    time. Without them the fragmented mov muxer silently rebases everything to 0 and -copyts is lost.
        //  - delay_moov: AC-3 / E-AC-3 copy can't write the init segment until it has parsed a packet.
        a.AddRange(
        [
            "-max_muxing_queue_size", "2048", "-strict", "-2",
            "-avoid_negative_ts", "make_non_negative", "-use_editlist", "0",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof+delay_moov+frag_discont",
            "-frag_duration", "2000000",
            "pipe:1",
        ]);
        return a;
    }

    /// <summary>Capability key the browser reports, plus the RFC 6381 string for addSourceBuffer().</summary>
    private static (string Key, string Codec)? VideoCodec(MediaStream v)
    {
        var profile = (v.Profile ?? string.Empty).ToLowerInvariant();
        var bits = v.BitDepth ?? 8;
        var level = v.Level is double l && l > 0 ? (int)l : 0;
        switch (v.Codec?.ToLowerInvariant())
        {
            case "h264":
                var lv = (level is > 0 and < 256 ? level : 40).ToString("X2", CultureInfo.InvariantCulture);
                if (profile.Contains("4:4:4", StringComparison.Ordinal) || profile.Contains("4:2:2", StringComparison.Ordinal))
                {
                    return ("h264:high422", "avc1.7A00" + lv);
                }

                if (profile.Contains("high 10", StringComparison.Ordinal) || bits > 8)
                {
                    return ("h264:high10", "avc1.6E00" + lv);
                }

                if (profile.Contains("high", StringComparison.Ordinal) || profile.Length == 0)
                {
                    return ("h264:high", "avc1.6400" + lv);
                }

                if (profile.Contains("main", StringComparison.Ordinal))
                {
                    return ("h264:main", "avc1.4D40" + lv);
                }

                return ("h264:baseline", "avc1.42E0" + lv);
            case "hevc":
                var hl = level > 0 ? level : 120;
                if (profile.Contains("main 10", StringComparison.Ordinal) || (profile.Length == 0 && bits == 10))
                {
                    return ("hevc:main10", $"hvc1.2.4.L{hl}.B0");
                }

                return profile is "main" or "" ? ("hevc:main", $"hvc1.1.6.L{hl}.B0") : ("hevc:rext", $"hvc1.4.10.L{hl}.B0");
            case "av1":
                if (profile.Contains("high", StringComparison.Ordinal) || profile.Contains("professional", StringComparison.Ordinal))
                {
                    return ("av1:high", "av01.1.08M.10");
                }

                var al = level is > 0 and < 32 ? level : 8;
                return bits > 8
                    ? ("av1:10", $"av01.0.{al:D2}M.10")
                    : ("av1:8", $"av01.0.{al:D2}M.08");
            case "vp9":
                if (profile.Contains('2', StringComparison.Ordinal))
                {
                    return ("vp9:2", "vp09.02.40.10");
                }

                return profile.Contains('0', StringComparison.Ordinal) || profile.Length == 0
                    ? ("vp9:0", "vp09.00.40.08")
                    : ("vp9:other", "vp09.01.40.08");
            default:
                return null; // mpeg2, vc1, mpeg4 part 2, ...: always transcode
        }
    }

    private static (string Key, string Codec)? AudioCodec(MediaStream a) => a.Codec?.ToLowerInvariant() switch
    {
        "aac" => ("aac", "mp4a.40.2"),
        "mp3" => ("mp3", "mp4a.6B"),
        "flac" => ("flac", "flac"),
        "opus" => ("opus", "opus"),
        "ac3" => ("ac3", "ac-3"),
        "eac3" => ("eac3", "ec-3"),
        "alac" => ("alac", "alac"),
        _ => null, // truehd, dts, vorbis, pcm, ...: always transcode
    };

    private static string Label(MediaStream? s)
    {
        if (s is null)
        {
            return "the source";
        }

        var codec = s.Codec?.ToLowerInvariant() switch
        {
            "h264" => "H.264", "hevc" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "mpeg2video" => "MPEG-2",
            "vc1" => "VC-1", "mpeg4" => "MPEG-4", "aac" => "AAC", "ac3" => "Dolby Digital", "eac3" => "Dolby Digital+",
            "truehd" => "TrueHD", "dts" => "DTS", "flac" => "FLAC", "mp3" => "MP3", "opus" => "Opus", "alac" => "ALAC",
            "vorbis" => "Vorbis", null => "unknown", var c when c.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
            var c => c.ToUpperInvariant(),
        };

        if (s.Type == MediaStreamType.Video)
        {
            var parts = new List<string> { codec };
            if (!string.IsNullOrEmpty(s.Profile) && codec is "H.264" or "HEVC")
            {
                parts.Add(s.Profile);
            }

            if (s.Width is int w)
            {
                parts.Add(w >= 3800 ? "4K" : w >= 1900 ? "1080p" : w >= 1260 ? "720p" : $"{s.Height}p");
            }

            if (s.VideoRange == VideoRange.HDR)
            {
                parts.Add(s.VideoRangeType.ToString());
            }

            return string.Join(' ', parts);
        }

        var ch = s.Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", int n => $"{n}ch", _ => null };
        return ch is null ? codec : $"{codec} {ch}";
    }
}

/// <summary>
/// One running ffmpeg process pumping fragmented MP4 into the socket.
/// Flow control is credit based: the page grants bytes as it appends them to its SourceBuffer, and stops
/// granting once it holds enough seconds ahead. With no credit we stop reading stdout, the pipe fills, and
/// ffmpeg blocks — so a paused tab costs no CPU and the browser never hits its MSE quota.
/// </summary>
internal sealed class StreamSession : IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;
    private const long MaxCredit = 256L * 1024 * 1024;

    private readonly Process _process;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _creditSignal = new(0, int.MaxValue);
    private readonly Queue<string> _stderr = new();
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _sendBinary;
    private readonly Func<StreamSession, bool, string?, Task> _onEnd;
    private readonly ILogger _logger;
    private long _credit;
    private long _sent;
    private Task _pump = Task.CompletedTask;

    private StreamSession(
        uint sid,
        Process process,
        long credit,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendBinary,
        Func<StreamSession, bool, string?, Task> onEnd,
        ILogger logger,
        CancellationToken connectionToken)
    {
        Sid = sid;
        _process = process;
        _credit = Math.Clamp(credit, 64 * 1024, MaxCredit);
        _sendBinary = sendBinary;
        _onEnd = onEnd;
        _logger = logger;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
    }

    public uint Sid { get; }

    public static StreamSession Start(
        string ffmpeg,
        IReadOnlyList<string> args,
        uint sid,
        long credit,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendBinary,
        Func<StreamSession, bool, string?, Task> onEnd,
        ILogger logger,
        CancellationToken connectionToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // must be drained, or ffmpeg blocks once the stderr pipe fills
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg); // no shell quoting: paths like "Diary of a Wimpy Kid: Dog Days" are safe
        }

        var process = new Process { StartInfo = psi };
        var session = new StreamSession(sid, process, credit, sendBinary, onEnd, logger, connectionToken);
        process.ErrorDataReceived += (_, e) => session.OnStderr(e.Data);
        logger.LogDebug("WsStream: sid {Sid}: {Ffmpeg} {Args}", sid, ffmpeg, string.Join(' ', args));
        process.Start();
        process.BeginErrorReadLine();
        session._pump = Task.Run(session.PumpAsync);
        return session;
    }

    public void AddCredit(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        var now = Interlocked.Add(ref _credit, bytes);
        if (now > MaxCredit)
        {
            Interlocked.Exchange(ref _credit, MaxCredit);
        }

        _creditSignal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        Kill();
        try
        {
            await _pump.ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // Pump faults were already reported.
        }

        _process.Dispose();
        _creditSignal.Dispose();
        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        var frame = new byte[5 + ChunkSize];
        frame[0] = SocketConnection.FrameMedia;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), Sid);
        var stdout = _process.StandardOutput.BaseStream;
        bool ok;
        string? error = null;
        try
        {
            while (true)
            {
                while (Interlocked.Read(ref _credit) <= 0)
                {
                    await _creditSignal.WaitAsync(ct).ConfigureAwait(false);
                }

                var want = (int)Math.Min(ChunkSize, Interlocked.Read(ref _credit));
                var n = await stdout.ReadAsync(frame.AsMemory(5, want), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                Interlocked.Add(ref _credit, -n);
                _sent += n;
                await _sendBinary(frame.AsMemory(0, 5 + n), ct).ConfigureAwait(false);
            }

            await _process.WaitForExitAsync(ct).ConfigureAwait(false);
            ok = _process.ExitCode == 0;
            if (!ok)
            {
                error = StderrTail() ?? $"ffmpeg exited with code {_process.ExitCode}";
            }
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a seek / new track, or the socket closed: no end message
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ok = false;
            error = ex.Message;
        }

        if (ok)
        {
            _logger.LogInformation("WsStream: sid {Sid} finished, {MB:0.0} MB sent", Sid, _sent / 1048576d);
        }
        else
        {
            _logger.LogWarning("WsStream: sid {Sid} failed after {MB:0.0} MB: {Error}", Sid, _sent / 1048576d, error);
        }

        await _onEnd(this, ok, error).ConfigureAwait(false);
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Never started or already gone.
        }
    }

    private void OnStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stderr)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > 8)
            {
                _stderr.Dequeue();
            }
        }
    }

    private string? StderrTail()
    {
        lock (_stderr)
        {
            return _stderr.Count == 0 ? null : string.Join('\n', _stderr);
        }
    }
}
