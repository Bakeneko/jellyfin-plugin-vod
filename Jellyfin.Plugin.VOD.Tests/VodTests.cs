using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VOD.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests
{
    public class VodTests
    {
        [Fact]
        public void PluginConfiguration_DefaultsYtDlpCallsPerMinuteTo20()
        {
            // 120/min earned a rate-limit window in production; 20 keeps a factor of six of
            // margin and still allows ~28 800 calls a day per platform.
            Assert.Equal(20, new PluginConfiguration().YtDlpCallsPerMinute);
        }

        [Fact]
        public void PluginConfiguration_DefaultsTheResolutionSettings()
        {
            var config = new PluginConfiguration();

            // One wave is one minute of a platform's budget at the default rate, which makes the
            // two dials readable together.
            Assert.Equal(20, config.ResolutionWaveSize);

            // Fifteen minutes, doubling: a transient failure costs minutes, not a day.
            Assert.Equal(15, config.ResolutionRetryMinutes);
        }
    }
}
