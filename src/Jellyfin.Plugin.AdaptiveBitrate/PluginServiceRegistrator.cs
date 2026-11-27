using AdaptiveBitrate.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AdaptiveBitrate;

/// <summary>Enregistre les services et les filtres du plugin.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<SwitchLog>();
        services.AddSingleton<Sessions>();
        services.AddSingleton<HlsFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<IStartupFilter, ScriptInjection>();
        services.Configure<MvcOptions>(o =>
        {
            o.Filters.AddService<HlsFilter>(int.MaxValue - 50);
            o.Filters.AddService<PlaybackInfoFilter>(int.MaxValue - 50);
        });
    }
}
