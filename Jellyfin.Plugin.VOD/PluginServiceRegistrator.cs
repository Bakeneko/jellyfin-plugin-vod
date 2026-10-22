using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Localization;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
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

                var dbPath = Path.Combine(Plugin.Instance!.EnsureDataFolder(), "vod.db");
                var db = new VodDatabase(dbPath, logger);
                db.EnsureCreated();
                return db;
            });

            // Streaming services
            serviceCollection.AddSingleton<CookieJar>();
            serviceCollection.AddSingleton(provider => new YtDlpThrottle(
                TimeProvider.System,
                () => Plugin.Instance?.Configuration.YtDlpCallsPerMinute ?? 20,
                (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                provider.GetRequiredService<ILogger<YtDlpThrottle>>()));
            serviceCollection.AddSingleton<YtDlpService>();
            serviceCollection.AddSingleton<PlaylistCache>();
            serviceCollection.AddSingleton<SubtitleTokens>();
            serviceCollection.AddSingleton<MediaStreamWriter>();
            serviceCollection.AddSingleton<StreamDescriber>();

            // The two seams the publication pass is tested against. Registered as a resolution of
            // the concrete singleton, not as a second instance: the describer holds a cache and
            // the yt-dlp service holds the throttle, so two instances would each pace separately.
            serviceCollection.AddSingleton<IYtDlpService>(sp => sp.GetRequiredService<YtDlpService>());
            serviceCollection.AddSingleton<IStreamDescriber>(sp => sp.GetRequiredService<StreamDescriber>());

            // Localisation
            serviceCollection.AddSingleton<IVodLocalizer, VodLocalizer>();

            // Generated artwork, written next to the plugin's database
            serviceCollection.AddSingleton(
                _ => new ArtworkStore(Plugin.Instance!.EnsureDataFolder()));

            // Publication services
            serviceCollection.AddSingleton<LibraryPublisher>();
            serviceCollection.AddSingleton<StreamDescriptionStep>();
            serviceCollection.AddSingleton<LiveProbeService>();

            // The pass and its five steps.
            serviceCollection.AddSingleton<ListingStep>();
            serviceCollection.AddSingleton<CleanupStep>();
            serviceCollection.AddSingleton<WaveStep>();
            serviceCollection.AddSingleton<CheckupStep>();
            serviceCollection.AddSingleton<PublicationService>();

            // Through the concrete singleton, never AddSingleton<IPublicationPass,
            // PublicationService>: that form builds a SECOND PublicationService, and the pass owns
            // the per-pass state — the refused lanes, the sources already listed this pass — which
            // two instances would each believe they alone held.
            serviceCollection.AddSingleton<IPublicationPass>(
                sp => sp.GetRequiredService<PublicationService>());

            // The configuration is read through a delegate rather than injected, because a task
            // outlives any number of administrator edits. Registered rather than closed over,
            // because the task is built by Jellyfin's activator, not by a factory of ours.
            serviceCollection.AddSingleton<Func<PluginConfiguration?>>(
                _ => static () => Plugin.Instance?.Configuration);

            // No IScheduledTask registration. Jellyfin does not read them: it scans the assembly
            // for types implementing the interface and builds each one with
            // ActivatorUtilities.CreateInstance against this container
            // (ApplicationHost.cs:376 and :334). Everything a task's constructor asks for must
            // therefore be registered ABOVE, and a registration here would only build a second,
            // unused copy of each task.
            //
            // The failure mode is not quiet, and it is not local: CreateInstanceSafe logs the
            // error and then calls FailPlugin, which writes Malfunctioned into the plugin's
            // manifest — and IsEnabledAndSupported requires at least Active, so the WHOLE plugin
            // is skipped at the next start. PublicationTask shipped this way once.
            // PluginServiceRegistratorTests.EveryScheduledTask_CanBeActivated_TheWayJellyfinActivatesIt
            // is what guards it.
        }
    }
}
