using System.IO;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD
{
    /// <summary>Registers all plugin services with the Jellyfin DI container.</summary>
    public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(
            IServiceCollection serviceCollection,
            IServerApplicationHost applicationHost)
        {
            // Data layer
            serviceCollection.AddSingleton<IVodDatabase>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<VodDatabase>>();
                var dbPath = Path.Combine(Plugin.Instance!.DataFolderPath, "vod.db");
                var db = new VodDatabase(dbPath, logger);
                db.EnsureCreated();
                return db;
            });

            // Streaming services
            serviceCollection.AddSingleton<CookieJar>();
            serviceCollection.AddSingleton<YtDlpService>();
            serviceCollection.AddSingleton<PlaylistCache>();

            // Localisation
            serviceCollection.AddSingleton<IVodLocalizer, VodLocalizer>();

            // Sync services
            serviceCollection.AddSingleton<LibraryWriter>();
            serviceCollection.AddSingleton<SyncService>();

            // Scheduled tasks
            serviceCollection.AddSingleton<IScheduledTask, SyncTask>();
        }
    }
}
