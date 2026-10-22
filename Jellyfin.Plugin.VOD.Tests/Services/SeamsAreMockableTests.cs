using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    /// <summary>
    /// Proves the two seams can actually be substituted, which is the whole reason they exist.
    /// </summary>
    /// <remarks>
    /// Written because a plan review found that every test of the new publication pass assumed a
    /// fake yt-dlp service and a fake describer, while both classes were sealed with no interface —
    /// so those tests could not have compiled, or would have launched a real yt-dlp, which is
    /// forbidden here after a rate-limit incident. This test fails the moment that regresses.
    /// </remarks>
    public sealed class SeamsAreMockableTests
    {
        [Fact]
        public async Task TheYtDlpSeamCanBeSubstituted_AndItsMetadataFailureCarriesItsReason()
        {
            var yt = Substitute.For<IYtDlpService>();
            yt.GetMetadataAsync("https://example.test/v", Arg.Any<CancellationToken>())
              .Returns(new MetadataResult(
                  MetadataOutcome.Failed,
                  new VideoMetadata { Id = "v", Availability = "needs_auth" },
                  StdErr: "ERROR: Sign in to confirm your age",
                  TimedOut: false));

            var result = await yt.GetMetadataAsync("https://example.test/v", default);

            Assert.Equal("needs_auth", result.Metadata!.Availability);
            Assert.StartsWith("ERROR:", result.StdErr, StringComparison.Ordinal);
        }

        [Fact]
        public void TheConcreteTypesStillSatisfyTheSeams()
        {
            // Guards against a seam drifting away from the class it was extracted from.
            Assert.True(typeof(IYtDlpService).IsAssignableFrom(typeof(YtDlpService)));
            Assert.True(typeof(IStreamDescriber).IsAssignableFrom(typeof(StreamDescriber)));
        }
    }
}
