using System.IO;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD
{
    /// <summary>Registers VOD services with the Jellyfin DI container.</summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(
            IServiceCollection serviceCollection,
            IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<IVodDatabase>(sp =>
            {
                var paths = sp.GetRequiredService<IApplicationPaths>();
                var logger = sp.GetRequiredService<ILogger<VodDatabase>>();
                var db = new VodDatabase(Path.Combine(paths.DataPath, "vod.db"), logger);
                db.EnsureCreated();
                return db;
            });

            serviceCollection.AddSingleton<CookieJar>();
            serviceCollection.AddSingleton<YtDlpService>();
            serviceCollection.AddSingleton<PlaylistCache>();
        }
    }
}
