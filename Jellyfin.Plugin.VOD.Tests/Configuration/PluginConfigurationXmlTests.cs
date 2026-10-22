using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.VOD.Configuration;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Configuration
{
    /// <summary>
    /// Compatibility of the persisted settings file across the rework's renames.
    /// </summary>
    /// <remarks>
    /// Jellyfin persists a plugin's configuration with a plain
    /// <see cref="XmlSerializer"/> (<c>Emby.Server.Implementations/Serialization/MyXmlSerializer.cs</c>,
    /// <c>new XmlSerializer(t)</c>), which DISCARDS an element it does not recognise without
    /// raising anything. A renamed property therefore loses whatever the administrator had set,
    /// silently, and the next save on the configuration page makes the loss permanent.
    /// <para>
    /// These tests use the same serializer rather than a substitute, because the behaviour under
    /// test is the serializer's, not ours.
    /// </para>
    /// </remarks>
    public sealed class PluginConfigurationXmlTests
    {
        private static T Deserialize<T>(string xml)
        {
            using var reader = new StringReader(xml);
            return (T)new XmlSerializer(typeof(T)).Deserialize(reader)!;
        }

        private static string Serialize<T>(T value)
        {
            using var writer = new StringWriter();
            new XmlSerializer(typeof(T)).Serialize(writer, value);
            return writer.ToString();
        }

        [Fact]
        public void AFileWrittenBeforeTheRename_KeepsTheWaveSizeAndRetryMinutes()
        {
            // A deliberate wave of 5 and a retry of 120 minutes are the settings of someone who has
            // already been rate-limited. Dropped, they become 20 and 15 — four times the videos per
            // turn and eight times the retry frequency — which is how the incident repeats.
            var config = Deserialize<PluginConfiguration>(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PluginConfiguration>
                  <ResolutionWaveSize>5</ResolutionWaveSize>
                  <ResolutionRetryMinutes>120</ResolutionRetryMinutes>
                </PluginConfiguration>
                """);

            Assert.Equal(5, config.WaveSize);
            Assert.Equal(120, config.RetryMinutes);
        }

        [Fact]
        public void AFileWrittenBeforeTheRename_KeepsEachSourcesListingInterval()
        {
            // Per source, and the value that matters most: 2880 minutes is the two-day interval set
            // after the 2026-10-12 rate limit. Dropped, it becomes 720 and the source is listed
            // four times more often than configured, with nothing in the log or the page saying so.
            var config = Deserialize<PluginConfiguration>(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PluginConfiguration>
                  <Sources>
                    <SourceDefinition>
                      <Url>https://www.youtube.com/@chan/videos</Url>
                      <SyncIntervalMinutes>2880</SyncIntervalMinutes>
                    </SourceDefinition>
                  </Sources>
                </PluginConfiguration>
                """);

            var source = Assert.Single(config.Sources);
            Assert.Equal(2880, source.ListingIntervalMinutes);
        }

        [Fact]
        public void ASave_WritesOnlyTheCurrentNames()
        {
            // The shim reads the old file; it must not keep writing the old vocabulary, or the
            // legacy element lives for ever and a later reader has two sources of truth.
            var config = new PluginConfiguration { WaveSize = 7, RetryMinutes = 45 };
            config.Sources.Add(new SourceDefinition { Url = "https://example/x", ListingIntervalMinutes = 60 });

            var xml = Serialize(config);

            Assert.Contains("<WaveSize>7</WaveSize>", xml, System.StringComparison.Ordinal);
            Assert.Contains("<RetryMinutes>45</RetryMinutes>", xml, System.StringComparison.Ordinal);
            Assert.Contains("<ListingIntervalMinutes>60</ListingIntervalMinutes>", xml, System.StringComparison.Ordinal);

            Assert.DoesNotContain("ResolutionWaveSize", xml, System.StringComparison.Ordinal);
            Assert.DoesNotContain("ResolutionRetryMinutes", xml, System.StringComparison.Ordinal);
            Assert.DoesNotContain("SyncIntervalMinutes", xml, System.StringComparison.Ordinal);
        }
    }
}
