using System.Linq;
using Jellyfin.Plugin.VOD;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Imaging;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests
{
    /// <summary>
    /// The dependency-injection wiring.
    /// </summary>
    /// <remarks>
    /// Every other test in this project builds its subject with <c>new</c>, so none of them can
    /// tell whether the server would be able to build it at all. A missing registration is not a
    /// compile error: it surfaces at startup as a plugin that loads and then does nothing, which
    /// is the hardest failure of this rework to notice and the cheapest to prevent.
    /// <para>
    /// The two registrations that reach <c>Plugin.Instance</c> — the database path and the artwork
    /// folder — are replaced here, because there is no plugin instance in a unit test. Everything
    /// else is resolved as the server would resolve it.
    /// </para>
    /// </remarks>
    public sealed class PluginServiceRegistratorTests
    {
        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();

            // What Jellyfin itself puts in the container before a plugin registers anything.
            services.AddLogging();
            services.AddHttpClient();
            services.AddSingleton(Substitute.For<IServerConfigurationManager>());
            services.AddSingleton(Substitute.For<ILibraryManager>());
            services.AddSingleton(Substitute.For<IMediaStreamRepository>());
            services.AddSingleton(Substitute.For<IMediaEncoder>());
            services.AddSingleton(Substitute.For<IServerApplicationHost>());

            new PluginServiceRegistrator().RegisterServices(
                services, Substitute.For<IServerApplicationHost>());

            // The two Plugin.Instance-dependent factories. Registered last, so these win.
            services.AddSingleton(Substitute.For<IVodDatabase>());
            services.AddSingleton(new ArtworkStore(System.IO.Path.GetTempPath()));

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                // What makes this test worth running: the container is asked to prove every
                // registration can actually be built, rather than only the ones resolved below.
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        [Fact]
        public void ThePass_AndItsFiveSteps_AreResolvable()
        {
            using var provider = Build();

            Assert.NotNull(provider.GetRequiredService<ListingStep>());
            Assert.NotNull(provider.GetRequiredService<CleanupStep>());
            Assert.NotNull(provider.GetRequiredService<WaveStep>());
            Assert.NotNull(provider.GetRequiredService<CheckupStep>());
            Assert.NotNull(provider.GetRequiredService<PublicationService>());
        }

        /// <summary>
        /// Every scheduled task the assembly declares must be constructible the way Jellyfin
        /// actually constructs it.
        /// </summary>
        /// <remarks>
        /// Jellyfin does not read the container's <c>IScheduledTask</c> registrations. It scans the
        /// assembly for types implementing the interface and builds each one with
        /// <c>ActivatorUtilities.CreateInstance</c> against the service provider
        /// (<c>ApplicationHost.cs:376</c> and <c>:334</c>). A constructor parameter the container
        /// cannot supply therefore defeats the task however it is registered — and the failure is
        /// not quiet: <c>CreateInstanceSafe</c> logs, then calls <c>FailPlugin</c>, which writes
        /// <c>Malfunctioned</c> into the plugin's manifest. Since
        /// <c>IsEnabledAndSupported</c> requires a status of at least <c>Active</c>, the whole
        /// plugin is then skipped at the next start.
        /// <para>
        /// This is how PublicationTask shipped invisible: it took an <c>IPublicationPass</c> and a
        /// <c>Func&lt;PluginConfiguration?&gt;</c> that nothing registered, and the test that was
        /// here asserted the container held two <c>IScheduledTask</c> registrations — true, and
        /// irrelevant, because Jellyfin never looks at them.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryScheduledTask_CanBeActivated_TheWayJellyfinActivatesIt()
        {
            using var provider = Build();

            var taskTypes = typeof(PluginServiceRegistrator).Assembly
                .GetTypes()
                .Where(t => typeof(IScheduledTask).IsAssignableFrom(t)
                    && t is { IsAbstract: false, IsInterface: false })
                .ToList();

            // Two, and two only. There is no stream-description task: describing happens inside
            // the wave, with the metadata fetch that found the manifest. A third would mean the
            // retired one came back, with a second stream of yt-dlp calls the throttle cannot see.
            Assert.Equal(2, taskTypes.Count);

            foreach (var type in taskTypes)
            {
                var task = (IScheduledTask)ActivatorUtilities.CreateInstance(provider, type);

                // The identity Jellyfin shows in its task list. A task whose Key is empty cannot
                // have its triggers persisted.
                Assert.False(string.IsNullOrWhiteSpace(task.Key));
                Assert.False(string.IsNullOrWhiteSpace(task.Name));
                Assert.NotEmpty(task.GetDefaultTriggers());
            }
        }

        [Fact]
        public void ThePassSeam_ResolvesToTheOneAndOnlyPublicationService()
        {
            // Through the concrete singleton, not a second instance of it: the pass owns the
            // per-pass state — the refused lanes, the sources already listed — and two of them
            // would each believe they were the only one running.
            using var provider = Build();

            Assert.Same(
                provider.GetRequiredService<PublicationService>(),
                provider.GetRequiredService<IPublicationPass>());
        }

        [Fact]
        public void TheScheduledTasks_CarryDistinctKeys()
        {
            // Jellyfin persists a task's triggers against its key. Two tasks sharing one would
            // have the second silently inherit the first's schedule.
            using var provider = Build();

            var keys = typeof(PluginServiceRegistrator).Assembly
                .GetTypes()
                .Where(t => typeof(IScheduledTask).IsAssignableFrom(t)
                    && t is { IsAbstract: false, IsInterface: false })
                .Select(t => ((IScheduledTask)ActivatorUtilities.CreateInstance(provider, t)).Key)
                .ToList();

            Assert.Equal(keys.Count, keys.Distinct(System.StringComparer.Ordinal).Count());
        }

        [Fact]
        public void TheTwoSeams_ResolveToTheSameInstanceAsTheirConcreteClass()
        {
            // The yt-dlp service holds the call throttle and the describer holds the playlist
            // cache. A second instance of either would mean a second throttle — the rate ceiling
            // doubled, against a platform that has already rate-limited this plugin once.
            using var provider = Build();

            Assert.Same(
                provider.GetRequiredService<YtDlpService>(),
                provider.GetRequiredService<IYtDlpService>());
            Assert.Same(
                provider.GetRequiredService<StreamDescriber>(),
                provider.GetRequiredService<IStreamDescriber>());
        }

        [Fact]
        public void ThePass_IsASingleton_SoItsPerSourceLocksAreShared()
        {
            using var provider = Build();

            Assert.Same(
                provider.GetRequiredService<PublicationService>(),
                provider.GetRequiredService<PublicationService>());
        }
    }
}
