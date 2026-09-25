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

public sealed class Plugin : BasePlugin<BasePluginConfiguration>
{
    public static readonly Guid PluginId = new("b937e205-49b9-4941-9199-e21b8bf3b568");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "WS Stream";

    public override Guid Id => PluginId;

    public override string Description => "Login, search and media over one WebSocket at <BaseUrl>" + SocketHost.PathSuffix;
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SocketHost>();
        serviceCollection.AddTransient<IStartupFilter, SocketStartupFilter>();
    }
}

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
                branch.UseForwardedHeaders();

                branch.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
                branch.Run(ctx => ctx.RequestServices.GetRequiredService<SocketHost>().HandleAsync(ctx));
            });

            _logger.LogInformation("WsStream: WebSocket endpoint mapped at <BaseUrl>{Path}", SocketHost.PathSuffix);
            next(app);
        };
    }
}
