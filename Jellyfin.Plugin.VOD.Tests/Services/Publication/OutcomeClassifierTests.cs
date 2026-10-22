using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The outcome classifier is what replaced the bare word "Failed" for fourteen of sixteen
    /// stuck videos on the test instance. Each test pins one cause, so a regression that silently
    /// stops naming a cause is immediately visible as a failure on that cause's test rather than
    /// as a mystery in the database.
    /// </summary>
    public sealed class OutcomeClassifierTests
    {
        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static VideoMetadata WithAvailability(string availability)
            => new() { Id = "x", Availability = availability };

        private static VideoMetadata Unavailable()
            // FormatCount=0, LiveStatus defaults to NotLive → IsUnavailable=true
            => new() { Id = "x", FormatCount = 0 };

        // ── Happy path ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Succeeded_ReturnsOk_WithNullDetail()
        {
            // A clean answer resets the retry counter. No detail avoids storing a meaningless
            // empty string in the database for every successfully fetched video.
            var result = MetadataResult.From(new VideoMetadata { Id = "x", FormatCount = 1 });

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.Ok, outcome.Outcome);
            Assert.Null(outcome.Detail);
            Assert.False(outcome.CountsAgainstTheVideo);
        }

        // ── Decision order ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Timeout_WinsOver_ADescriptiveStdErr()
        {
            // Measured: a rate-limited session stops responding rather than reporting an error.
            // The timeout IS the signal; a concurrent stderr that names something else is noise.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Video unavailable",
                TimedOut: true);

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.Timeout, outcome.Outcome);
        }

        [Fact]
        public void Availability_WinsOver_AContradictoryStdErr()
        {
            // A structured field that survived from the metadata fetch is more trustworthy than
            // prose that changes between yt-dlp versions. This is the exact scenario that was
            // invisible on the test instance: availability=needs_auth was present but discarded.
            var result = new MetadataResult(
                MetadataOutcome.Failed,
                WithAvailability("needs_auth"),
                StdErr: "ERROR: Requested format is not available");

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.NeedsAuth, outcome.Outcome);
        }

        // ── Per-outcome: one case each ───────────────────────────────────────────────────────

        [Fact]
        public void NeedsAuth_FromAvailability_NeedsAuth()
        {
            // needs_auth is the detector for an expired cookie session: one such video on an
            // instance that has a cookie file configured means the whole session has expired.
            var result = new MetadataResult(MetadataOutcome.Failed, WithAvailability("needs_auth"));

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.NeedsAuth, outcome.Outcome);
            Assert.Equal("needs_auth", outcome.Detail);
            Assert.True(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void NeedsAuth_FromAvailability_SubscriberOnly()
        {
            var result = new MetadataResult(MetadataOutcome.Failed, WithAvailability("subscriber_only"));

            Assert.Equal(CallOutcome.NeedsAuth, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void NeedsAuth_FromAvailability_PremiumOnly()
        {
            var result = new MetadataResult(MetadataOutcome.Failed, WithAvailability("premium_only"));

            Assert.Equal(CallOutcome.NeedsAuth, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void NeedsAuth_FromStdErr_SignIn()
        {
            // Real yt-dlp line: "ERROR: Sign in to confirm your age or that you are not a bot"
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Sign in to confirm your age or that you are not a bot");

            Assert.Equal(CallOutcome.NeedsAuth, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void NoFormats_FromStdErr()
        {
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Requested format is not available");

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.NoFormats, outcome.Outcome);
            Assert.True(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void Unavailable_FromAvailability_Private()
        {
            // "private" is a platform decision, not a transient error: the video was made private
            // and remains so until the owner changes it. Counted against the video.
            var result = new MetadataResult(MetadataOutcome.Failed, WithAvailability("private"));

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.Unavailable, outcome.Outcome);
            Assert.Equal("private", outcome.Detail);
            Assert.True(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void Unavailable_FromStdErr_VideoUnavailable()
        {
            // Measured on ELa4xqlV8sw: yt-dlp writes "Video unavailable" then exits 1.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Video unavailable");

            Assert.Equal(CallOutcome.Unavailable, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void GeoBlocked_FromStdErr()
        {
            // yt-dlp's standard geo-block message.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: This video is not available in your country.");

            Assert.Equal(CallOutcome.GeoBlocked, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void RateLimited_FromStdErr_429()
        {
            // Real yt-dlp line used in YtDlpServiceTests fixtures.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Unable to download JSON metadata: HTTP Error 429: Too Many Requests");

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.RateLimited, outcome.Outcome);
            // A rate limit is the platform's doing, not the video's — do not advance the backoff.
            Assert.False(outcome.CountsAgainstTheVideo);
        }

        // ── Regression: "age" substring collisions (F1 fix) ─────────────────────────────────

        [Fact]
        public void RateLimited_Webpage429_IsNotMisclassifiedAsNeedsAuth()
        {
            // THE defect that motivated tightening the NeedsAuth patterns. "webpage" contains
            // "age", so the bare "age" pattern matched this common rate-limit message and
            // returned NeedsAuth — advancing every video's backoff and raising a spurious
            // "cookie session expired" warning on the configuration page.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Unable to download webpage: HTTP Error 429: Too Many Requests");

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.RateLimited, outcome.Outcome);
            Assert.False(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void SubtitleLanguageError_IsNotMisclassifiedAsNeedsAuth()
        {
            // "language" contains "age". A subtitle-extraction warning must not be read as
            // an authentication failure.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "WARNING: Unable to extract subtitle language");

            Assert.NotEqual(CallOutcome.NeedsAuth, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void ThumbnailImageError_IsNotMisclassifiedAsNeedsAuth()
        {
            // "image" contains "age". A thumbnail-download failure must not be read as an
            // authentication failure.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Unable to download thumbnail image");

            Assert.NotEqual(CallOutcome.NeedsAuth, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void NeedsAuth_SignInConfirmAge_StillWorks()
        {
            // Verify that the tightened phrases still catch a real age-restriction message after
            // removing the bare "age" substring.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: [youtube] abc: Sign in to confirm your age");

            Assert.Equal(CallOutcome.NeedsAuth, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void Timeout_FromTimedOut_Flag()
        {
            // The caller sets TimedOut when the process had to be killed. A separate first line of
            // stderr is preserved as the detail, but the outcome is always Timeout.
            var result = new MetadataResult(MetadataOutcome.Failed, null, TimedOut: true);

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.Timeout, outcome.Outcome);
            // A timeout is a session-level event, not the video's fault.
            Assert.False(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void ExtractorError_FromUnrecognisedStdErr()
        {
            // An unrecognised error must NOT be silently mapped to a named cause — that is
            // exactly the silent-default bug this class replaces. The raw text is preserved.
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: [youtube] x: Some brand-new error yt-dlp invented");

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.ExtractorError, outcome.Outcome);
            Assert.True(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void ParseError_WhenNoMetadataAndNoStdErr()
        {
            // Exit 0 with output that did not parse as valid JSON: no stderr, no metadata.
            // This is the only failure path that carries no stderr at all.
            var result = new MetadataResult(MetadataOutcome.Failed, null, StdErr: null);

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal(CallOutcome.ParseError, outcome.Outcome);
            Assert.True(outcome.CountsAgainstTheVideo);
        }

        [Fact]
        public void NetworkError_FromStdErr_UrlOpenError()
        {
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: urlopen error [Errno -2] Name or service not known");

            Assert.Equal(CallOutcome.NetworkError, OutcomeClassifier.Classify(result).Outcome);
        }

        // ── Detail extraction ───────────────────────────────────────────────────────────────

        [Fact]
        public void Detail_IsFirstLine_OfMultiLineStdErr()
        {
            // The first line carries the ERROR: message; the rest is a stack trace. Only the
            // first line is stored — the rest adds length without adding diagnosability.
            const string MultiLine = "ERROR: Video unavailable\nTraceback (most recent call last):\n  File ...\n";
            var result = new MetadataResult(MetadataOutcome.Failed, null, StdErr: MultiLine);

            var outcome = OutcomeClassifier.Classify(result);

            Assert.Equal("ERROR: Video unavailable", outcome.Detail);
        }

        [Fact]
        public void Detail_IsTruncated_WhenFirstLineExceeds500Characters()
        {
            // The database column caps at 500 characters. Truncating here prevents silent loss at
            // the write layer and makes the cap explicit in the code that produces the value.
            var longLine = "ERROR: " + new string('x', 600);
            var result = new MetadataResult(MetadataOutcome.Failed, null, StdErr: longLine);

            var outcome = OutcomeClassifier.Classify(result);

            Assert.NotNull(outcome.Detail);
            Assert.True(outcome.Detail!.Length <= 500);
        }

        [Fact]
        public void NullOrEmptyStdErr_DoesNotThrow()
        {
            // A caller that passes an empty stderr (which is valid: some processes exit non-zero
            // and write nothing) must get back a ParseError, not an exception.
            var resultNull = new MetadataResult(MetadataOutcome.Failed, null, StdErr: null);
            var resultEmpty = new MetadataResult(MetadataOutcome.Failed, null, StdErr: string.Empty);

            Assert.Equal(CallOutcome.ParseError, OutcomeClassifier.Classify(resultNull).Outcome);
            Assert.Equal(CallOutcome.ParseError, OutcomeClassifier.Classify(resultEmpty).Outcome);
        }

        [Fact]
        public void UnrecognisedStdErr_PreservesRawTextInDetail()
        {
            // An unknown error that loses its text is no better than the word "Failed".
            const string Raw = "ERROR: [youtube] xyz: extractor raised SomeFutureException";
            var result = new MetadataResult(MetadataOutcome.Failed, null, StdErr: Raw);

            Assert.Equal(Raw, OutcomeClassifier.Classify(result).Detail);
        }

        // ── CountsAgainstTheVideo ────────────────────────────────────────────────────────────

        [Fact]
        public void RateLimited_DoesNotCount()
        {
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: HTTP Error 429: Too Many Requests");

            Assert.False(OutcomeClassifier.Classify(result).CountsAgainstTheVideo);
        }

        [Fact]
        public void Timeout_DoesNotCount()
        {
            var result = new MetadataResult(MetadataOutcome.Failed, null, TimedOut: true);

            Assert.False(OutcomeClassifier.Classify(result).CountsAgainstTheVideo);
        }

        [Fact]
        public void NeedsAuth_Counts()
        {
            var result = new MetadataResult(MetadataOutcome.Failed, WithAvailability("needs_auth"));

            Assert.True(OutcomeClassifier.Classify(result).CountsAgainstTheVideo);
        }

        [Fact]
        public void Unavailable_Counts()
        {
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: Video unavailable");

            Assert.True(OutcomeClassifier.Classify(result).CountsAgainstTheVideo);
        }

        [Fact]
        public void ExtractorError_Counts()
        {
            var result = new MetadataResult(
                MetadataOutcome.Failed, null,
                StdErr: "ERROR: some future extractor error");

            Assert.True(OutcomeClassifier.Classify(result).CountsAgainstTheVideo);
        }

        // ── Availability table — public does NOT decide ──────────────────────────────────────

        [Fact]
        public void Availability_Public_FallsThrough_ToStdErr()
        {
            // "public" means the video is accessible; it does not cause a failure on its own.
            // The real cause comes from stderr (here: a format error).
            var result = new MetadataResult(
                MetadataOutcome.Failed,
                WithAvailability("public"),
                StdErr: "ERROR: Requested format is not available");

            Assert.Equal(CallOutcome.NoFormats, OutcomeClassifier.Classify(result).Outcome);
        }

        [Fact]
        public void Availability_Unlisted_FallsThrough_ToStdErr()
        {
            var result = new MetadataResult(
                MetadataOutcome.Failed,
                WithAvailability("unlisted"),
                StdErr: "ERROR: Video unavailable");

            Assert.Equal(CallOutcome.Unavailable, OutcomeClassifier.Classify(result).Outcome);
        }

        // ── ChannelOffAir ────────────────────────────────────────────────────────────────────

        [Fact]
        public void ChannelOffAir_MapsTo_ExtractorError_WithExplanatoryDetail()
        {
            // The live probe handles ChannelOffAir and never reaches this classifier. Receiving
            // it here means a channel URL was mistaken for a video URL — naming that is more
            // honest than forwarding the ChannelOffAir value, which has no CallOutcome equivalent.
            var outcome = OutcomeClassifier.Classify(MetadataResult.ChannelOffAir);

            Assert.Equal(CallOutcome.ExtractorError, outcome.Outcome);
            Assert.NotNull(outcome.Detail);
            Assert.NotEmpty(outcome.Detail!);
            Assert.True(outcome.CountsAgainstTheVideo);
        }

        // ── Edge cases ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void Succeeded_WithIsUnavailable_DoesNotCrash_AndClassifiesAsFailure()
        {
            // Interpret already reclassifies this combination as Failed, so it cannot arrive in
            // normal operation. The guard ensures the classifier degrades gracefully rather than
            // returning Ok for an unplayable video.
            var meta = Unavailable(); // IsUnavailable=true
            var result = new MetadataResult(MetadataOutcome.Succeeded, meta, StdErr: null);

            var outcome = OutcomeClassifier.Classify(result);

            Assert.NotEqual(CallOutcome.Ok, outcome.Outcome);
        }
    }
}
