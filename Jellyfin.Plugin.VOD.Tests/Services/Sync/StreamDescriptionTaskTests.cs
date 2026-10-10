using System.Net.Http;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class StreamDescriptionTaskTests
    {
        private readonly StreamDescriptionTask _task;

        public StreamDescriptionTaskTests()
        {
            // Real collaborators: none of them does any I/O at construction.
            var cache = new PlaylistCache();
            var describer = new StreamDescriber(
                Substitute.For<ILibraryManager>(),
                Substitute.For<IMediaStreamRepository>(),
                Substitute.For<IMediaEncoder>(),
                cache,
                new SubtitleTokens(cache),
                NullLogger<StreamDescriber>.Instance);
            var service = new StreamDescriptionService(
                Substitute.For<IVodDatabase>(),
                describer,
                new YtDlpService(new CookieJar(NullLogger<CookieJar>.Instance), NullLogger<YtDlpService>.Instance),
                Substitute.For<IHttpClientFactory>(),
                NullLogger<StreamDescriptionService>.Instance);

            _task = new StreamDescriptionTask(service, NullLogger<StreamDescriptionTask>.Instance);
        }

        [Fact]
        public void Key_IsStable()
        {
            // Jellyfin stores a task's triggers under its key: changing it orphans the stored
            // trigger and resets the administrator's chosen interval.
            Assert.Equal("VodStreamDescription", _task.Key);
        }

        [Fact]
        public void GetDefaultTriggers_IsAFixedFifteenMinuteInterval()
        {
            var trigger = Assert.Single(_task.GetDefaultTriggers());

            Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
            Assert.Equal(System.TimeSpan.FromMinutes(15).Ticks, trigger.IntervalTicks);
        }
    }
}
