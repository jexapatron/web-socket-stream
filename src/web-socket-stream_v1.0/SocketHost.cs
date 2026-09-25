using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WsStream;

/// <summary>Accepts sockets on <see cref="PathSuffix"/> and owns the Jellyfin services connections use.</summary>
public sealed class SocketHost
{
    /// <summary>
    /// Matched as a suffix so any BaseUrl works. It sits under /socket on purpose: the stock Jellyfin nginx
    /// config already sends upgrade headers for "location /socket", which is a prefix match.
    /// </summary>
    public const string PathSuffix = "/socket/wsstream";

    private readonly ILogger<SocketHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="SocketHost"/> class.</summary>
    public SocketHost(
        ILoggerFactory loggerFactory,
        INetworkManager network,
        ISessionManager sessions,
        IUserManager users,
        ILibraryManager library,
        IUserViewManager userViews,
        IMediaSourceManager mediaSources,
        IMediaEncoder encoder,
        IImageProcessor images,
        IServerApplicationHost appHost)
    {
        LoggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SocketHost>();
        Network = network;
        Sessions = sessions;
        Users = users;
        Encoder = encoder;
        MediaSources = mediaSources;
        AppHost = appHost;
        Library = new LibraryService(library, userViews, images);
    }

    internal ILoggerFactory LoggerFactory { get; }

    internal INetworkManager Network { get; }

    internal ISessionManager Sessions { get; }

    internal IUserManager Users { get; }

    internal IMediaEncoder Encoder { get; }

    internal IMediaSourceManager MediaSources { get; }

    internal IServerApplicationHost AppHost { get; }

    internal LibraryService Library { get; }

    /// <summary>Path predicate used by the startup filter.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>True when the request targets our socket.</returns>
    public static bool IsOurPath(HttpContext ctx)
        => ctx.Request.Path.Value is { } p && p.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Handles one request on our path.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>A task that completes when the socket closes.</returns>
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            // Not an HTTP API: plain requests get told to upgrade and nothing else.
            ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            ctx.Response.Headers.Upgrade = "websocket";
            await ctx.Response.WriteAsync("WS Stream: WebSocket upgrade required.\n").ConfigureAwait(false);
            return;
        }

        var ip = ctx.GetNormalizedRemoteIP();

        // We run before Jellyfin's IP filter / "Allow remote connections" middleware, so enforce it here.
        if (!ctx.IsLocal() && Network.ShouldAllowServerAccess(ip) != RemoteAccessPolicyResult.Allow)
        {
            _logger.LogWarning("WsStream: refused {IP} (Jellyfin remote access / IP filter policy)", ip);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        WebSocket ws;
        try
        {
            ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "WsStream: handshake with {IP} failed", ip);
            return;
        }

        _logger.LogInformation("WsStream: {IP} connected", ip);
        var conn = new SocketConnection(this, ws, ip, LoggerFactory.CreateLogger<SocketConnection>());
        try
        {
            await conn.RunAsync(ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Client went away.
        }
#pragma warning disable CA1031 // A socket fault must never take the request pipeline down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "WsStream: connection from {IP} failed", ip);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            ws.Dispose();
            _logger.LogInformation("WsStream: {IP} disconnected", ip);
        }
    }
}
