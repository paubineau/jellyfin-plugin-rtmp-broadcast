using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RtmpBroadcast;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<BroadcastService>();
        services.AddHostedService(provider => provider.GetRequiredService<BroadcastService>());
        services.AddSingleton<IStartupFilter, PlayerUiStartupFilter>();
        services.AddHttpContextAccessor();
        services.Configure<MvcOptions>(options => options.Filters.Add(new ClientPlaybackReport()));
    }
}
