using System.Linq;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class StreamDescriptionTaskTests
    {
        private readonly StreamDescriptionTask _task =
            new(null!, NullLogger<StreamDescriptionTask>.Instance);

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
