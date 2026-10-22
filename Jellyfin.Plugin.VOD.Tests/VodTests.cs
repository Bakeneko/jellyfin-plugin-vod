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

        /// <summary>
        /// The pass's dials. Thin on purpose, but these are values three tasks downstream nobody
        /// re-derives: a default that drifted would change how often every source is listed and how
        /// much the plugin spends, with nothing failing to say so.
        /// </summary>
        [Fact]
        public void PluginConfiguration_DefaultsThePassSettings()
        {
            var config = new PluginConfiguration();

            // One wave is one minute of a platform's budget at the default rate, which makes the
            // two dials readable together.
            Assert.Equal(20, config.WaveSize);

            // Fifteen minutes, doubling: a transient failure costs minutes, not a day.
            Assert.Equal(15, config.RetryMinutes);

            // A week of latency on a removal, weighed against one request per 30 videos.
            Assert.Equal(7, config.FullListingIntervalDays);

            Assert.Equal(10, config.ListingsPerPass);

            // Everything on one 30-day clock measured at ~248 calls a day on the configured
            // sources — fewer than the 266 the separate unavailable re-check used to cost alone.
            Assert.Equal(30, config.MetadataMaxAgeDays);
        }

        [Fact]
        public void PluginConfiguration_PinsNoMetadataLanguageByDefault()
        {
            // Empty is a choice, not a missing value: nothing is passed to yt-dlp and the published
            // title stays whatever each channel calls original. Inferring one from the server's
            // locale would silently rewrite every published title on an upgrade.
            Assert.Equal(string.Empty, new PluginConfiguration().PreferredMetadataLanguage);
        }

        [Fact]
        public void SourceDefinition_DefaultsItsListingIntervalToTwelveHours()
        {
            // Carried over unchanged from the setting it renames, so an existing configuration's
            // cadence is not silently altered by the rework.
            Assert.Equal(720, new SourceDefinition().ListingIntervalMinutes);
        }
    }
}
