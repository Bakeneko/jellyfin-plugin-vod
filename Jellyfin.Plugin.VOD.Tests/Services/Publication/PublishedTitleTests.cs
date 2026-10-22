using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The two functions that decide what title a video is published under, and whether a
    /// listing has revealed a retitle event. Each test pins exactly one decision point:
    /// a silent regression here can rename every video in a library, or repeatedly refetch
    /// a platform that never sends listing titles.
    /// </summary>
    public sealed class PublishedTitleTests
    {
        // ── For: title selection ─────────────────────────────────────────────────────────────

        [Fact]
        public void LocalizedTitle_IsPreferred_WhenBothExistAndDiffer()
        {
            // The listing returns the localized title honoring youtube:lang; the metadata call
            // always returns the original language and ignores that setting. Using the listing
            // title ensures the published name matches what the user configured.
            var meta = new VideoMetadata
            {
                Id = "PM6lMoClzy8",
                ExtractorKey = "Youtube",
                Title = "Original title"
            };

            var title = PublishedTitle.For("Titre localisé", meta, "PM6lMoClzy8");

            Assert.Equal("Titre localisé", title);
        }

        [Fact]
        public void ArteFallback_UsesMetadataTitle_WhenListingCarriesNone()
        {
            // Measured 2026-08-19: an Arte flat listing carries only ie_key, url and two other
            // fields — no title at all. Without this fallback every Arte video would be published
            // under its native id (e.g. "130515-005-A"), which is unreadable.
            var meta = new VideoMetadata
            {
                Id = "130515-005-A",
                ExtractorKey = "ArteTV",
                Title = "Pourquoi éclate-t-on en sanglots ?"
            };

            var title = PublishedTitle.For(null, meta, "130515-005-A");

            Assert.Equal("Pourquoi éclate-t-on en sanglots ?", title);
        }

        [Fact]
        public void DeadEntry_UsesNativeId_WhenNeitherListingNorMetadataHaveATitle()
        {
            // A dead YouTube playlist entry carries id, url, ie_key and thumbnails only — no
            // title. Measured 2026-08-19: exactly 10 of 43 entries of one playlist were untitled,
            // and those were the 10 YouTube itself had marked unavailable. If a metadata call is
            // also impossible, the native id is all that remains to keep the item identifiable.
            var title = PublishedTitle.For(null, null, "PM6lMoClzy8");

            Assert.Equal("PM6lMoClzy8", title);
        }

        [Fact]
        public void BlankListingTitle_IsTreatedAsAbsent()
        {
            // A whitespace-only title must not be published — an item named "   " would appear
            // nameless in Jellyfin. The blank is treated as absent so the metadata fallback fires.
            var meta = new VideoMetadata
            {
                Id = "PM6lMoClzy8",
                ExtractorKey = "Youtube",
                Title = "Real title"
            };

            var title = PublishedTitle.For("   ", meta, "PM6lMoClzy8");

            Assert.Equal("Real title", title);
        }

        [Fact]
        public void Result_IsTrimmed()
        {
            // A title with leading or trailing whitespace would make the item's display name differ
            // from what the administrator sees when editing the listing — a subtle inconsistency
            // that is hard to notice and reproduce.
            var title = PublishedTitle.For("  Some video title  ", null, "x");

            Assert.Equal("Some video title", title);
        }

        [Fact]
        public void TwitchVod_UsesFullTitle_InsteadOfDatedTitle()
        {
            // Measured 2026-10-03 (spec F15): yt-dlp appends the broadcast date to a TwitchVod
            // title field, so it changes after each archive. fulltitle holds the real stream name
            // and stays stable. PlatformRules.TitleFor is called rather than reading Title directly
            // so that this exception lives in exactly one place and cannot diverge.
            //
            // Listing-first order is safe here: Twitch has no usable flat listing (the flat mode
            // does not carry live_status), so a Twitch video always arrives with a null listing
            // title, and this fallback applies.
            var meta = new VideoMetadata
            {
                Id = "v1234567890",
                ExtractorKey = "TwitchVod",
                Title = "Awesome Poggers — 2026-10-03",
                FullTitle = "Awesome Poggers"
            };

            var title = PublishedTitle.For(null, meta, "v1234567890");

            Assert.Equal("Awesome Poggers", title);
        }

        [Fact]
        public void TwitchStream_UsesDescription_AsTheStreamName()
        {
            // Measured 2026-10-03 (spec F15): a live channel's title field is generic
            // ("channelname (live) — 2026-10-03") while the actual stream name sits in description.
            // Same listing-first safety as TwitchVod: no flat listing title ever arrives for Twitch.
            var meta = new VideoMetadata
            {
                Id = "stream123",
                ExtractorKey = "TwitchStream",
                Title = "otplol_ (live) — 2026-10-03",
                Description = "Playing chess against my chat"
            };

            var title = PublishedTitle.For(null, meta, "stream123");

            Assert.Equal("Playing chess against my chat", title);
        }

        // ── HasBeenRetitled ──────────────────────────────────────────────────────────────────

        [Fact]
        public void HasBeenRetitled_True_WhenListingTitleChanges()
        {
            // The core case: the platform updated the video's title and the listing now returns
            // something different from what was stored at the previous pass.
            var result = PublishedTitle.HasBeenRetitled("Old title", "New title");

            Assert.True(result);
        }

        [Fact]
        public void HasBeenRetitled_False_WhenFreshListingTitleIsNull()
        {
            // Absence is not a change. Arte returns no listing title at all (four fields per entry,
            // none of which is title). Treating null as a retitle would refetch all ~300 Arte
            // videos on every pass, forever.
            var result = PublishedTitle.HasBeenRetitled("Stored title", null);

            Assert.False(result);
        }

        [Fact]
        public void HasBeenRetitled_False_WhenFreshListingTitleIsBlank()
        {
            // Same invariant as the null case. A whitespace-only string is not an authoritative
            // new title; it must not overwrite a stored value or trigger a refetch.
            var result = PublishedTitle.HasBeenRetitled("Stored title", "   ");

            Assert.False(result);
        }

        [Fact]
        public void HasBeenRetitled_True_WhenStoredIsNullAndFreshIsPresent()
        {
            // The previous pass had no listing title. We do not know what the video was called
            // before, so a fetch is the safe answer. It costs one call; after that the stored
            // title is filled and the detector stays silent.
            var result = PublishedTitle.HasBeenRetitled(null, "Some title");

            Assert.True(result);
        }

        [Fact]
        public void HasBeenRetitled_False_WhenTitlesMatchAfterTrim()
        {
            // A title that differs only in surrounding whitespace is not a retitle — it is a
            // formatting artifact of the listing and must not trigger an unnecessary refetch.
            var result = PublishedTitle.HasBeenRetitled("My title", "  My title  ");

            Assert.False(result);
        }

        [Fact]
        public void HasBeenRetitled_True_OnCaseDifferenceOnly()
        {
            // The comparison is ordinal, not case-insensitive. A case-only change IS a retitle:
            // it changes the title that will be published and is therefore observable by the user.
            var result = PublishedTitle.HasBeenRetitled("my title", "My Title");

            Assert.True(result);
        }
    }
}
