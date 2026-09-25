using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WsStream;

/// <summary>Plugin entry point. No configuration page; everything lives on the socket.</summary>
public sealed class Plugin : BasePlugin<BasePluginConfiguration>
{
    /// <summary>Must match "guid" in meta.json.</summary>
    public static readonly Guid PluginId = new("b937e205-49b9-4941-9199-e21b8bf3b568");

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "WS Stream";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Login, search and media over one WebSocket at <BaseUrl>" + SocketHost.PathSuffix;
}

/// <summary>Registers the socket host and the startup filter with Jellyfin's DI container.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SocketHost>();
        serviceCollection.AddTransient<IStartupFilter, SocketStartupFilter>();
    }
}

/// <summary>
/// Puts our WebSocket branch in front of Jellyfin's whole pipeline.
/// Jellyfin's WebSocketHandlerMiddleware takes *every* upgrade request regardless of path and rejects it
/// unless a token is in the handshake, so a normal plugin controller can never accept a socket, and
/// login-over-the-socket is impossible on Jellyfin's own /socket.
/// </summary>
internal sealed class SocketStartupFilter : IStartupFilter
{
    private readonly ILogger<SocketStartupFilter> _logger;

    public SocketStartupFilter(ILogger<SocketStartupFilter> logger) => _logger = logger;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.MapWhen(SocketHost.IsOurPath, branch =>
            {
                // Same KnownProxies rules Jellyfin applies, so per-user "remote access" checks see the real client IP.
                branch.UseForwardedHeaders();

                // Server-side ping frames every 20 s keep reverse proxies (nginx 60 s, Cloudflare 100 s) from idling us out.
                branch.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
                branch.Run(ctx => ctx.RequestServices.GetRequiredService<SocketHost>().HandleAsync(ctx));
            });

            _logger.LogInformation("WsStream: WebSocket endpoint mapped at <BaseUrl>{Path}", SocketHost.PathSuffix);
            next(app);
        };
    }
}
