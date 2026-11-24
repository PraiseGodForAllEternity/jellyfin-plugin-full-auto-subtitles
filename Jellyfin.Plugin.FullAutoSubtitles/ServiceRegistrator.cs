using Jellyfin.Plugin.FullAutoSubtitles.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FullAutoSubtitles;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<StateStore>();
        services.AddSingleton<SubtitleNeeds>();
        services.AddSingleton<TrackService>();
        services.AddSingleton<TimingService>();
        services.AddSingleton<FetchService>();
        services.AddHostedService(p => p.GetRequiredService<FetchService>());
    }
}
