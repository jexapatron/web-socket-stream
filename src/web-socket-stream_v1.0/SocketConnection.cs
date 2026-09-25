using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using AuthenticationException = MediaBrowser.Controller.Authentication.AuthenticationException;

namespace Jellyfin.Plugin.WsStream;

/// <summary>
/// One browser tab. Text frames carry JSON control messages; binary frames are
/// [u8 kind][u32 BE id][payload] where kind 1 = media bytes for stream id, kind 2 = image for request id.
/// </summary>
internal sealed partial class SocketConnection : IAsyncDisposable
{
    internal const byte FrameMedia = 1;
    internal const byte FrameImage = 2;

    private const string AppName = "WS Stream";
    private const string AppVersion = "1.0.0";
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly TimeSpan SignInWindow = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SocketHost _host;
    private readonly WebSocket _ws;
    private readonly IPAddress _ip;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _workers = new(3, 3);
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private HashSet<string> _caps = new(StringComparer.Ordinal);
    private User? _user;
    private string? _token;
    private StreamSession? _stream;

    public SocketConnection(SocketHost host, WebSocket ws, IPAddress ip, ILogger logger)
    {
        _host = host;
        _ws = ws;
        _ip = ip;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _cts.Token);
        var ct = linked.Token;

        await SendJsonAsync(
            new { t = "hello", proto = 1, server = _host.AppHost.FriendlyName, version = _host.AppHost.ApplicationVersionString },
            ct).ConfigureAwait(false);

        _ = EnforceSignInWindowAsync(ct);

        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>(16 * 1024);
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            message.ResetWrittenCount();
            ValueWebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                    return;
                }

                if (message.WrittenCount + r.Count > MaxMessageBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.").ConfigureAwait(false);
                    return;
                }

                message.Write(buffer.AsSpan(0, r.Count));
            }
            while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Text)
            {
                await DispatchAsync(message.WrittenMemory, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await StopStreamAsync().ConfigureAwait(false);
        _cts.Dispose();
        _sendLock.Dispose();
        _workers.Dispose();
        _streamLock.Dispose();
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, "That message wasn't valid JSON.", ct).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var t = Str(m, "t");
            var rid = Int(m, "rid");
            switch (t)
            {
                case "ping":
                    await SendJsonAsync(new { t = "pong" }, ct).ConfigureAwait(false);
                    return;
                case "login":
                    await LoginAsync(m, ct).ConfigureAwait(false);
                    return;
                case "resume":
                    await ResumeAsync(m, ct).ConfigureAwait(false);
                    return;
            }

            var user = _user;
            if (user is null)
            {
                await SendErrorAsync(rid, "Sign in first.", ct).ConfigureAwait(false);
                return;
            }

            switch (t)
            {
                case "credit":
                    var stream = _stream;
                    if (stream is not null && stream.Sid == UInt(m, "sid"))
                    {
                        stream.AddCredit(Long(m, "n") ?? 0);
                    }

                    break;
                case "caps":
                    SetCaps(m);
                    break;
                case "stop":
                    await StopStreamAsync().ConfigureAwait(false);
                    break;
                case "play":
                    await PlayAsync(user, m, ct).ConfigureAwait(false);
                    break;
                case "logout":
                    await LogoutAsync(ct).ConfigureAwait(false);
                    break;
                case "views" or "search" or "kids" or "img":
                    // Library + image work runs off the receive loop so credit messages are never stuck behind it.
                    RunWork(user, t, m.Clone(), rid, ct);
                    break;
                default:
                    await SendErrorAsync(rid, $"Unknown message type \"{t}\".", ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task LoginAsync(JsonElement m, CancellationToken ct)
    {
        var username = Str(m, "user");
        var device = DeviceId(m);
        if (string.IsNullOrWhiteSpace(username) || device is null)
        {
            await SendJsonAsync(new { t = "auth", ok = false, err = "Username and a device id are required." }, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            // Same path as POST /Users/AuthenticateByName: lockout counters, disabled accounts, remote-access
            // and parental-schedule rules all apply, and the session shows up under Dashboard > Devices.
            var result = await _host.Sessions.AuthenticateNewSession(new AuthenticationRequest
            {
                Username = username,
                Password = Str(m, "pw") ?? string.Empty,
                App = AppName,
                AppVersion = AppVersion,
                DeviceId = device,
                DeviceName = DeviceName(m),
                RemoteEndPoint = _ip.ToString(),
            }).ConfigureAwait(false);

            var user = _host.Users.GetUserById(result.User.Id);
            if (user is null)
            {
                throw new AuthenticationException("User vanished.");
            }

            SetCaps(m);
            await CompleteAuthAsync(user, result.AccessToken, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            _logger.LogInformation("WsStream: failed sign-in for {User} from {IP}", username, _ip);
            await Task.Delay(1000, ct).ConfigureAwait(false); // Jellyfin's lockout still counts; this just slows guessing
            await SendJsonAsync(new { t = "auth", ok = false, err = "Wrong username or password." }, ct).ConfigureAwait(false);
        }
        catch (SecurityException ex)
        {
            await SendJsonAsync(new { t = "auth", ok = false, err = ex.Message }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await SendJsonAsync(new { t = "auth", ok = false, err = ex.Message }, ct).ConfigureAwait(false);
        }
    }

    private async Task ResumeAsync(JsonElement m, CancellationToken ct)
    {
        var token = Str(m, "token");
        var device = DeviceId(m);
        if (string.IsNullOrEmpty(token) || token.Length > 128 || device is null)
        {
            await SendJsonAsync(new { t = "auth", ok = false, err = "Token and device id are required." }, ct).ConfigureAwait(false);
            return;
        }

        var session = await _host.Sessions.GetSessionByAuthenticationToken(token, device, _ip.ToString()).ConfigureAwait(false);
        var user = session is null ? null : _host.Users.GetUserById(session.UserId);
        if (user is null)
        {
            await SendJsonAsync(new { t = "auth", ok = false, expired = true, err = "Saved sign-in has expired. Sign in again." }, ct).ConfigureAwait(false);
            return;
        }

        var denied = Denied(user);
        if (denied is not null)
        {
            await SendJsonAsync(new { t = "auth", ok = false, err = denied }, ct).ConfigureAwait(false);
            return;
        }

        SetCaps(m);
        await CompleteAuthAsync(user, token, ct).ConfigureAwait(false);
    }

    private async Task CompleteAuthAsync(User user, string token, CancellationToken ct)
    {
        _user = user;
        _token = token;
        _logger.LogInformation("WsStream: {User} signed in from {IP}", user.Username, _ip);
        await SendJsonAsync(
            new
            {
                t = "auth",
                ok = true,
                user = user.Username,
                token,
                admin = user.HasPermission(PermissionKind.IsAdministrator),
                caps = _caps.Order(StringComparer.Ordinal).ToArray(),
            },
            ct).ConfigureAwait(false);
    }

    private async Task LogoutAsync(CancellationToken ct)
    {
        await StopStreamAsync().ConfigureAwait(false);
        if (_token is not null)
        {
            await _host.Sessions.Logout(_token).ConfigureAwait(false); // revokes the token server-side
        }

        _user = null;
        _token = null;
        await SendJsonAsync(new { t = "auth", ok = false, signedOut = true }, ct).ConfigureAwait(false);
    }

    private async Task PlayAsync(User user, JsonElement m, CancellationToken ct)
    {
        var sid = UInt(m, "sid");
        var rid = Int(m, "rid");
        if (sid is null || !Guid.TryParse(Str(m, "id"), out var id))
        {
            await SendErrorAsync(rid, "play needs \"id\" and \"sid\".", ct).ConfigureAwait(false);
            return;
        }

        await StopStreamAsync().ConfigureAwait(false);

        // Re-read the user: an admin may have disabled the account or changed policy since sign-in.
        user = _host.Users.GetUserById(user.Id) ?? user;
        var denied = Denied(user) ?? (user.HasPermission(PermissionKind.EnableMediaPlayback) ? null : "Your Jellyfin account isn't allowed to play media.");
        var item = denied is null ? _host.Library.GetPlayable(user, id) : null;
        var source = item is null ? null : _host.MediaSources.GetStaticMediaSources(item, false, user).FirstOrDefault();
        string? error = denied ?? (source is null ? "That item isn't playable or isn't visible to you." : null);
        var plan = error is null ? StreamPlanner.Plan(item!, source!, _caps, Bool(m, "force"), user, out error) : null;
        if (plan is null)
        {
            await SendJsonAsync(new { t = "end", sid, ok = false, err = error }, ct).ConfigureAwait(false);
            return;
        }

        var start = Math.Max(0, Double(m, "start") ?? 0);
        await SendJsonAsync(
            new { t = "start", sid, id = item!.Id.ToString("N"), mime = plan.Mime, dur = plan.Duration, video = plan.HasVideo, start, info = plan.Description },
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "WsStream: {User} playing \"{Item}\" at {Start:0.0}s ({Mime}, video {V}, audio {A})",
            user.Username,
            item.Name,
            start,
            plan.Mime,
            plan.HasVideo ? (plan.VideoCopy ? "copy" : "transcode") : "none",
            plan.AudioCopy ? "copy" : "transcode");

        await _streamLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _stream = StreamSession.Start(
                _host.Encoder.EncoderPath,
                StreamPlanner.Arguments(plan, start),
                sid.Value,
                Long(m, "credit") ?? 4 * 1024 * 1024,
                SendBinaryAsync,
                OnStreamEndAsync,
                _logger,
                ct);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    private async Task OnStreamEndAsync(StreamSession session, bool ok, string? error)
    {
        try
        {
            await SendJsonAsync(new { t = "end", sid = session.Sid, ok, err = error }, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Socket already gone.
        }
    }

    private async Task StopStreamAsync()
    {
        StreamSession? old;
        await _streamLock.WaitAsync().ConfigureAwait(false);
        try
        {
            old = _stream;
            _stream = null;
        }
        finally
        {
            _streamLock.Release();
        }

        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false); // kills ffmpeg
        }
    }

    private void RunWork(User user, string t, JsonElement m, int? rid, CancellationToken ct)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _workers.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    switch (t)
                    {
                        case "views":
                            await SendJsonAsync(new { t = "results", rid, items = _host.Library.Views(user) }, ct).ConfigureAwait(false);
                            break;
                        case "search":
                            var q = Str(m, "q")?.Trim();
                            if (string.IsNullOrEmpty(q) || q.Length > 100)
                            {
                                await SendErrorAsync(rid, "Search needs 1–100 characters.", ct).ConfigureAwait(false);
                                break;
                            }

                            await SendJsonAsync(new { t = "results", rid, q, items = _host.Library.Search(user, q) }, ct).ConfigureAwait(false);
                            break;
                        case "kids":
                            var kids = Guid.TryParse(Str(m, "id"), out var pid) ? _host.Library.Kids(user, pid) : null;
                            if (kids is null)
                            {
                                await SendErrorAsync(rid, "That item doesn't exist or isn't visible to you.", ct).ConfigureAwait(false);
                                break;
                            }

                            await SendJsonAsync(new { t = "results", rid, parent = kids.Value.Parent, items = kids.Value.Items }, ct).ConfigureAwait(false);
                            break;
                        case "img":
                            var bytes = Guid.TryParse(Str(m, "id"), out var iid)
                                ? await _host.Library.ThumbAsync(user, iid, Int(m, "w") ?? 240).ConfigureAwait(false)
                                : null;
                            if (bytes is null || rid is null)
                            {
                                await SendJsonAsync(new { t = "img", rid, none = true }, ct).ConfigureAwait(false);
                                break;
                            }

                            var frame = new byte[5 + bytes.Length];
                            frame[0] = FrameImage;
                            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)rid.Value);
                            bytes.CopyTo(frame.AsSpan(5));
                            await SendBinaryAsync(frame, ct).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                    // Socket closed mid-request.
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(ex, "WsStream: {Type} request failed", t);
                    try
                    {
                        await SendErrorAsync(rid, $"{t} failed: {ex.Message}", ct).ConfigureAwait(false);
                    }
#pragma warning disable CA1031
                    catch
#pragma warning restore CA1031
                    {
                        // Nothing left to tell.
                    }
                }
                finally
                {
                    _workers.Release();
                }
            },
            ct);
    }

    /// <summary>Mirrors Jellyfin's per-request checks (DefaultAuthorizationHandler + AuthService).</summary>
    private string? Denied(User user)
    {
        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            return "This account is disabled.";
        }

        if (!user.HasPermission(PermissionKind.EnableRemoteAccess) && !_host.Network.IsInLocalNetwork(_ip))
        {
            return "This account can only be used on the local network.";
        }

        if (!user.HasPermission(PermissionKind.IsAdministrator) && !user.IsParentalScheduleAllowed())
        {
            return "This account isn't allowed access at this time.";
        }

        return null;
    }

    private void SetCaps(JsonElement m)
    {
        if (!m.TryGetProperty("caps", out var caps) || caps.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        _caps = caps.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => s.Length is > 0 and <= 16)
            .Take(32)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task EnforceSignInWindowAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SignInWindow, ct).ConfigureAwait(false);
            if (_user is null)
            {
                await CloseAsync(WebSocketCloseStatus.PolicyViolation, "Sign in within 60 seconds.").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Connection ended first.
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ws.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Peer vanished.
        }
        finally
        {
            _sendLock.Release();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
    }

    private Task SendJsonAsync(object value, CancellationToken ct)
        => SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text, ct);

    private Task SendErrorAsync(int? rid, string message, CancellationToken ct)
        => SendJsonAsync(new { t = "err", rid, msg = message }, ct);

    private Task SendBinaryAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        => SendAsync(frame, WebSocketMessageType.Binary, ct);

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        // WebSocket allows one concurrent send; media, images and replies all share this socket.
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.SendAsync(data, type, true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string? DeviceId(JsonElement m)
        => Str(m, "dev") is { } d && DeviceIdPattern().IsMatch(d) ? d : null;

    private static string DeviceName(JsonElement m)
    {
        var name = Str(m, "name")?.Trim();
        return string.IsNullOrEmpty(name) ? "WS Stream page" : name[..Math.Min(name.Length, 64)];
    }

    private static string? Str(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static uint? UInt(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var i) ? i : null;

    private static long? Long(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static double? Double(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static bool Bool(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex DeviceIdPattern();
}
