using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Controllers;
using Jellyfin.Plugin.VOD.Data.Models;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Controllers
{
    /// <summary>
    /// The two decisions the admin routes make. The controller itself takes sealed services and
    /// cannot be constructed under NSubstitute, so the decisions live apart from the wiring.
    /// </summary>
    public sealed class VodControllerTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-url")]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///etc/passwd")]
        [InlineData("ftp://example.test/x")]
        public void IsProbeableUrl_RejectsAnythingThatIsNotHttp(string? url)
        {
            // The route hands this URL to yt-dlp, which will fetch it. This check only rejects
            // schemes yt-dlp has no business seeing; what actually keeps the route from being an
            // SSRF primitive is its [Authorize(RequiresElevation)]. Do not drop that attribute on
            // the strength of this method.
            Assert.False(VodController.IsProbeableUrl(url));
        }

        [Theory]
        [InlineData("/etc/passwd")]
        [InlineData("file:///etc/passwd")]
        public void IsProbeableUrl_RejectsFileScheme(string url)
            => Assert.False(VodController.IsProbeableUrl(url));

        [Theory]
        [InlineData("https://www.youtube.com/@arte")]
        [InlineData("http://localhost:8096/whatever")]
        [InlineData("https://www.arte.tv/fr/videos/RC-027708/happy-valley/")]
        public void IsProbeableUrl_AcceptsAbsoluteHttpUrls(string url)
            => Assert.True(VodController.IsProbeableUrl(url));

        [Fact]
        public void ProjectLibraryNames_SortsForDisplay()
        {
            var names = VodController.ProjectLibraryNames(
            [
                new VirtualFolderInfo { Name = "VOD" },
                new VirtualFolderInfo { Name = "Films" },
                new VirtualFolderInfo { Name = "Séries" }
            ]);

            // Asserting "sorted", not a particular collation: CurrentCulture ordering is what
            // the UI wants, but pinning its exact output would make the test hostage to the CI
            // machine's locale.
            Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
            Assert.Equal(3, names.Count);
        }

        [Fact]
        public void ProjectLibraryNames_ReturnsEmptyRatherThanNull_WhenNoLibraryExists()
        {
            // The configuration page feeds this straight into a <select>; a null would break the
            // page rather than show an empty dropdown.
            Assert.Empty(VodController.ProjectLibraryNames([]));
        }

        [Fact]
        public void ProjectLibraryNames_SkipsALibraryWithNoUsableName()
        {
            var names = VodController.ProjectLibraryNames(
            [
                new VirtualFolderInfo { Name = "VOD" },
                new VirtualFolderInfo { Name = "   " },
                new VirtualFolderInfo { Name = null! }
            ]);

            Assert.Equal(new[] { "VOD" }, names);
        }

        [Fact]
        public void ProjectSourceStatus_CarriesWhatTheTableShows()
        {
            var synced = DateTimeOffset.UtcNow.AddHours(-2);

            var rows = VodController.ProjectSourceStatus(
            [
                new SourceRecord
                {
                    Url = "https://www.youtube.com/@arte",
                    Name = "ARTE",
                    LastSyncedAt = synced,
                    LastError = null,
                    VideoCount = 42
                }
            ]);

            var row = Assert.Single(rows);
            Assert.Equal("https://www.youtube.com/@arte", row.Url);
            Assert.Equal("ARTE", row.Name);
            Assert.Equal(synced, row.LastSyncedAt);
            Assert.Null(row.LastError);
            Assert.Equal(42, row.VideoCount);
        }

        [Fact]
        public void ProjectSourceStatus_KeepsAFailingSource()
        {
            // The whole point of the column: a source that never synced still has a row, and the
            // reason it failed travels with it.
            var rows = VodController.ProjectSourceStatus(
            [
                new SourceRecord
                {
                    Url = "https://www.youtube.com/@gone",
                    LastSyncedAt = null,
                    LastError = "No library named 'Vods'."
                }
            ]);

            var row = Assert.Single(rows);
            Assert.Null(row.LastSyncedAt);
            Assert.Equal("No library named 'Vods'.", row.LastError);
            Assert.Equal(0, row.VideoCount);
        }

        [Fact]
        public void ProjectSourceStatus_ReturnsEmptyRatherThanNull_WhenNothingIsConfigured()
            => Assert.Empty(VodController.ProjectSourceStatus([]));

        [Theory]
        [InlineData(new string[0], "")]
        [InlineData(new[] { "fr" }, "fr")]
        [InlineData(new[] { "fr", "en" }, "fr,en")]
        [InlineData(new[] { "fr", "", "en" }, "fr,en")]
        [InlineData(new[] { "fr", "   ", "en" }, "fr,en")]
        [InlineData(new[] { "", "  " }, "")]
        public void JoinLanguages_DropsBlankEntries(string[] input, string expected)
        {
            // Review Focus 4: a naive Join yields "fr,,en", and HlsPlaylistFilter then matches an
            // empty language prefix against every rendition.
            Assert.Equal(expected, VodController.JoinLanguages(input));
        }

        [Fact]
        public void JoinLanguages_TreatsNullAsEmpty()
            => Assert.Equal(string.Empty, VodController.JoinLanguages(null));
    }
}
