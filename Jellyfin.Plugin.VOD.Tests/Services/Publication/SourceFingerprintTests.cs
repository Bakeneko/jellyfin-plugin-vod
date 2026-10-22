using System;
using System.Globalization;
using System.Threading;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services.Publication;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The fingerprint that lets the pass detect a configuration change without storing one column
    /// per parameter. Each test pins exactly one invariant: a field that is silently dropped from
    /// the fingerprint would make configuration changes invisible to the pass, leaving sources
    /// permanently stuck on stale settings.
    /// </summary>
    public sealed class SourceFingerprintTests
    {
        // A fully-populated baseline that all field tests mutate exactly one field from.
        private static SourceDefinition Baseline() => new()
        {
            Url = "https://example.test/channel",
            Name = "My Channel",
            Enabled = true,
            Mode = SourceMode.Series,
            RetentionDays = 30,
            MaxVideosPerSource = 100,
            ListingIntervalMinutes = 720,
            Description = "A description",
        };

        // ── Each fingerprinted field changes the result ──────────────────────────────────────

        [Fact]
        public void ChangingEnabled_ChangesFingerprint()
        {
            // Enabled gates the pass: re-enabling a source after a long gap must trigger a
            // re-listing because its data may be stale. A fingerprint blind to this field would
            // keep a re-activated source frozen until the next full-listing cycle.
            var on = Baseline();
            var off = Baseline();
            off.Enabled = false;

            Assert.NotEqual(SourceFingerprint.Of(on), SourceFingerprint.Of(off));
        }

        [Fact]
        public void ChangingMode_ChangesFingerprint()
        {
            // Mode controls how videos are organised in Jellyfin. A change without a re-listing
            // leaves the library in the old shape until the next scheduled full listing; the
            // administrator would see no effect and no error.
            var series = Baseline();
            var movies = Baseline();
            movies.Mode = SourceMode.Movies;

            Assert.NotEqual(SourceFingerprint.Of(series), SourceFingerprint.Of(movies));
        }

        [Fact]
        public void ChangingMaxVideosPerSource_ChangesFingerprint()
        {
            // MaxVideosPerSource is the listing window. Raising it must bring in older videos;
            // lowering it must drop them. Neither effect happens without a new listing.
            var narrow = Baseline();
            var wide = Baseline();
            wide.MaxVideosPerSource = 200;

            Assert.NotEqual(SourceFingerprint.Of(narrow), SourceFingerprint.Of(wide));
        }

        [Fact]
        public void ChangingRetentionDays_ChangesFingerprint()
        {
            // RetentionDays is a publication filter that the checkup re-evaluates, but the checkup
            // only runs when the source has work pending. A source that is quiet would never be
            // revisited, and the new retention window would stay dark until the next full listing.
            var thirty = Baseline();
            var sixty = Baseline();
            sixty.RetentionDays = 60;

            Assert.NotEqual(SourceFingerprint.Of(thirty), SourceFingerprint.Of(sixty));
        }

        [Fact]
        public void ChangingName_ChangesFingerprint()
        {
            // Name is published on the series item. Same reasoning as Mode and RetentionDays:
            // the checkup that would apply a rename only runs when the source already has work to
            // do, so a quiet source would keep its old name until the next full-listing cycle.
            var original = Baseline();
            var renamed = Baseline();
            renamed.Name = "Renamed Channel";

            Assert.NotEqual(SourceFingerprint.Of(original), SourceFingerprint.Of(renamed));
        }

        // ── Fields outside the fingerprint do NOT change the result ──────────────────────────

        [Fact]
        public void ChangingUrl_DoesNotChangeFingerprint()
        {
            // Url is the primary key: a changed URL creates a new row and the old one is retired
            // by the "left the configuration" rule. Including it here would be misleading — it
            // would suggest that a URL change is handled by this mechanism when it is handled
            // elsewhere.
            var before = Baseline();
            var after = Baseline();
            after.Url = "https://example.test/other-channel";

            Assert.Equal(SourceFingerprint.Of(before), SourceFingerprint.Of(after));
        }

        [Fact]
        public void ChangingDescription_DoesNotChangeFingerprint()
        {
            // Description is an administrator note. Nothing publishes it, so changing it must not
            // trigger a re-listing that calls the platform with no reason.
            var before = Baseline();
            var after = Baseline();
            after.Description = "Updated description";

            Assert.Equal(SourceFingerprint.Of(before), SourceFingerprint.Of(after));
        }

        // ── Stability: same input always yields the same output ──────────────────────────────

        [Fact]
        public void SameConfiguration_ReturnsSameFingerprint()
        {
            // The fingerprint is compared against the stored one on every pass. A non-deterministic
            // result would re-list every source on every run, which is the defect the fingerprint
            // exists to prevent.
            var source = Baseline();

            Assert.Equal(SourceFingerprint.Of(source), SourceFingerprint.Of(source));
        }

        /// <summary>
        /// A tripwire, and — stated plainly — one with no teeth against today's field set.
        /// </summary>
        /// <remarks>
        /// Verified by mutation: replacing <c>InvariantCulture</c> with <c>CurrentCulture</c> in the
        /// implementation leaves the WHOLE suite green. The five fields are a bool, an enum and two
        /// integers, and none of them formats differently per culture — an enum and a bool are
        /// culture-neutral, and an integer's default format applies no group separator in any
        /// culture, contrary to what one might assume of a locale like hi-IN.
        /// <para>
        /// It is kept rather than deleted because it starts biting the day a culture-sensitive
        /// field joins the fingerprint — a decimal, a date — which is a realistic change, and the
        /// failure it would then catch is every source being re-listed after an OS locale update.
        /// Do not read a passing run here as proof that the implementation formats invariantly.
        /// </para>
        /// </remarks>
        [Fact]
        public void Fingerprint_IsStableUnderNonInvariantCulture()
        {
            var source = Baseline();
            var expectedFingerprint = SourceFingerprint.Of(source);

            var original = CultureInfo.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                Assert.Equal(expectedFingerprint, SourceFingerprint.Of(source));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        // ── Non-ambiguity: distinct configurations cannot produce the same fingerprint ────────

        [Fact]
        public void IntegerFieldsBoundary_DoNotCollideThroughNaiveConcatenation()
        {
            // Without a separator, integer fields can collide at digit boundaries:
            // Max=1, Retention=23 → concat "123" is identical to Max=12, Retention=3 → "123".
            // A separator between every field prevents this class of collision entirely.
            var a = Baseline();
            a.MaxVideosPerSource = 1;
            a.RetentionDays = 23;

            var b = Baseline();
            b.MaxVideosPerSource = 12;
            b.RetentionDays = 3;

            Assert.NotEqual(SourceFingerprint.Of(a), SourceFingerprint.Of(b));
        }

        [Fact]
        public void NameContainingSeparatorChar_DoesNotCollideThroughFieldShift()
        {
            // If the implementation used a visible separator (e.g. '|'), a Name that itself
            // contains '|' would create an extra segment — potentially matching a different
            // configuration whose Name ends one token earlier. The null-byte delimiter, forbidden
            // in XML text, prevents any user-supplied Name from crossing a field boundary.
            var withPipeInName = Baseline();
            withPipeInName.Name = "Channel|Extra";
            withPipeInName.RetentionDays = 0;

            var withoutPipe = Baseline();
            withoutPipe.Name = "Channel";
            withoutPipe.RetentionDays = 0;

            Assert.NotEqual(SourceFingerprint.Of(withPipeInName), SourceFingerprint.Of(withoutPipe));
        }

        // ── Null / empty Name safety ─────────────────────────────────────────────────────────

        [Fact]
        public void NullName_DoesNotThrow()
        {
            // SourceDefinition.Name initialises to string.Empty, but XML deserialization may set
            // the property to null. The function must not crash on a source that has never been
            // named, since it runs before any listing resolves the platform name.
            var source = Baseline();
            source.Name = null!;

            var exception = Record.Exception(() => SourceFingerprint.Of(source));

            Assert.Null(exception);
        }

        [Fact]
        public void EmptyName_DoesNotThrow()
        {
            // The default state before any name override is set. Empty and null must behave
            // identically so that a round-trip through XML deserialization does not change the
            // fingerprint.
            var source = Baseline();
            source.Name = string.Empty;

            var exception = Record.Exception(() => SourceFingerprint.Of(source));

            Assert.Null(exception);
        }

        [Fact]
        public void NullName_AndEmptyName_ProduceSameFingerprint()
        {
            // XML deserialization may produce null where the plugin initialises to string.Empty.
            // A fingerprint that differs between the two states would trigger a spurious re-listing
            // on the first pass after a serialization round-trip.
            var withNull = Baseline();
            withNull.Name = null!;

            var withEmpty = Baseline();
            withEmpty.Name = string.Empty;

            Assert.Equal(SourceFingerprint.Of(withNull), SourceFingerprint.Of(withEmpty));
        }
    }
}
