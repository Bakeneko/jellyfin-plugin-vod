using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Publication;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The function that decides which sources get listed in a turn and whether each listing is
    /// full or short. Each test pins one invariant; a regression that breaks any of them would
    /// either starve a source of its scheduled listing, list the wrong kind, or order the work
    /// in a way that makes an administrator's change wait many turns to take effect.
    /// </summary>
    public sealed class ListingSelectorTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        private static PluginConfiguration Config(int listingsPerPass = 10) => new()
        {
            FullListingIntervalDays = 7,
            ListingsPerPass = listingsPerPass,
        };

        private static SourceDefinition Source(string url = "https://example.test/s", bool enabled = true) => new()
        {
            Url = url,
            Enabled = enabled,
            ListingIntervalMinutes = 720,
        };

        /// <summary>
        /// A record that is fully up to date: both clocks are recent, and the fingerprint matches
        /// the source returned by <see cref="Source"/>.
        /// </summary>
        private static SourceRecord UpToDateRecord(SourceDefinition source) => new()
        {
            Url = source.Url,
            FullListedAt = Now.AddHours(-1),
            ShortListedAt = Now.AddMinutes(-10),
            ConfigFingerprint = SourceFingerprint.Of(source),
        };

        private static IReadOnlyDictionary<string, SourceRecord> KnownOf(params SourceRecord[] records) =>
            records.ToDictionary(r => r.Url, r => r, StringComparer.OrdinalIgnoreCase);

        // ── Each reason produces the correct listing kind ─────────────────────────────────────

        [Fact]
        public void ConfigurationChanged_IsSelectedAsFull()
        {
            // Only a full listing refreshes everything the checkup reads. Sending a short one
            // after a configuration change would let stale data linger until the next full cycle.
            var source = Source();
            var record = UpToDateRecord(source);
            record.ConfigFingerprint = "outdated-fingerprint"; // differs from SourceFingerprint.Of(source)

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.ConfigurationChanged, result[0].Reason);
            Assert.True(result[0].Full);
        }

        [Fact]
        public void NeverListed_IsSelectedAsFull()
        {
            // The first listing is always full: on an empty source every short entry would be
            // unknown and would escalate to a full anyway. Paying directly avoids the wasted call.
            var source = Source();
            var record = new SourceRecord { Url = source.Url, FullListedAt = null };

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.NeverListed, result[0].Reason);
            Assert.True(result[0].Full);
        }

        [Fact]
        public void FullListingDue_IsSelectedAsFull()
        {
            // Only a full listing catches a deletion or a retitle below the head. A short listing
            // is not a substitute: it sees only the most recent entries.
            var source = Source();
            var record = UpToDateRecord(source);
            record.FullListedAt = Now.AddDays(-8); // older than the 7-day full interval
            record.ShortListedAt = Now.AddMinutes(-10); // short is recent — rules out ShortListingDue

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.FullListingDue, result[0].Reason);
            Assert.True(result[0].Full);
        }

        [Fact]
        public void ShortListingDue_IsSelectedAsShort()
        {
            // A short listing is cheap (one platform request). It is only due here, not a full,
            // because the full cycle is still current.
            var source = Source();
            var record = UpToDateRecord(source);
            record.ShortListedAt = Now.AddMinutes(-800); // older than the 720-minute default interval

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.ShortListingDue, result[0].Reason);
            Assert.False(result[0].Full);
        }

        // ── Null stored fingerprint is NeverListed, not ConfigurationChanged ──────────────────

        [Fact]
        public void NullStoredFingerprint_IsNeverListed_NotConfigurationChanged()
        {
            // A null stored fingerprint means no checkup has ever succeeded, not that the
            // administrator changed something. Logging it as ConfigurationChanged would produce
            // false operator messages throughout the entire initial fill of ~300 sources.
            var source = Source();
            var record = new SourceRecord
            {
                Url = source.Url,
                FullListedAt = null,
                ConfigFingerprint = null, // never had a successful checkup
            };

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.NeverListed, result[0].Reason);
        }

        // ── A source whose clocks and fingerprint are current is not selected ─────────────────

        [Fact]
        public void UpToDateSource_IsNotSelected()
        {
            var source = Source();
            var record = UpToDateRecord(source);

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Empty(result);
        }

        // ── Listing interval is per source ────────────────────────────────────────────────────

        [Fact]
        public void PerSourceInterval_OnlyDueSourceIsSelected()
        {
            // The interval lives on SourceDefinition, not in a global setting, so two sources
            // listed at the same time are not due at the same time.
            var fastSource = Source("https://example.test/fast");
            fastSource.ListingIntervalMinutes = 5;

            var slowSource = Source("https://example.test/slow");
            slowSource.ListingIntervalMinutes = 1440;

            var listedAt = Now.AddMinutes(-10); // 10 minutes ago: past 5-minute interval, within 1440

            var fastRecord = UpToDateRecord(fastSource);
            fastRecord.ShortListedAt = listedAt;

            var slowRecord = UpToDateRecord(slowSource);
            slowRecord.ShortListedAt = listedAt;

            var result = ListingSelector.Select(
                [fastSource, slowSource],
                KnownOf(fastRecord, slowRecord),
                Config(),
                Now);

            Assert.Single(result);
            Assert.Equal(fastSource.Url, result[0].Source.Url);
        }

        // ── Cap keeps the most overdue, not the first encountered ─────────────────────────────

        [Fact]
        public void ListingsPerPass_KeepsMostOverdue_NotFirstEncountered()
        {
            // Inputs are deliberately given in reverse delay order so that a naive implementation
            // that takes the first N from the enumeration would pass the cap but fail to return the
            // right sources. A correct implementation sorts by delay and then applies the cap.
            var leastOverdue = Source("https://m.test/c"); // listed 8 days ago — due, but least overdue
            var middleOverdue = Source("https://a.test/b"); // listed 15 days ago
            var mostOverdue = Source("https://z.test/a");   // listed 30 days ago — most overdue

            var leastRecord = UpToDateRecord(leastOverdue);
            leastRecord.FullListedAt = Now.AddDays(-8);
            leastRecord.ShortListedAt = Now.AddMinutes(-10);

            var middleRecord = UpToDateRecord(middleOverdue);
            middleRecord.FullListedAt = Now.AddDays(-15);
            middleRecord.ShortListedAt = Now.AddMinutes(-10);

            var mostRecord = UpToDateRecord(mostOverdue);
            mostRecord.FullListedAt = Now.AddDays(-30);
            mostRecord.ShortListedAt = Now.AddMinutes(-10);

            // Input order: least overdue first, most overdue last — the hostile order.
            var result = ListingSelector.Select(
                [leastOverdue, middleOverdue, mostOverdue],
                KnownOf(leastRecord, middleRecord, mostRecord),
                Config(listingsPerPass: 2),
                Now);

            Assert.Equal(2, result.Count);
            Assert.Equal(mostOverdue.Url, result[0].Source.Url);
            Assert.Equal(middleOverdue.Url, result[1].Source.Url);
        }

        // ── ConfigurationChanged priority over NeverListed ────────────────────────────────────

        [Fact]
        public void ConfigurationChanged_SortsBefore_NeverListed_EvenWhenNeverListedIsOlder()
        {
            // NeverListed carries a null FullListedAt, which counts as infinite delay — it would
            // sort ahead of ConfigurationChanged in a pure delay sort. The reason-priority sort
            // must override that, because an administrator who just changed a setting should not
            // wait tens of turns behind sources that have simply never been listed.
            var configChangedSource = Source("https://b.test/changed");
            var changedRecord = UpToDateRecord(configChangedSource);
            changedRecord.ConfigFingerprint = "stale"; // mismatch forces ConfigurationChanged
            changedRecord.FullListedAt = Now.AddHours(-1); // recent, short delay

            var neverListedSource = Source("https://a.test/never"); // URL sorts first, so a naive sort would put it first
            var neverRecord = new SourceRecord
            {
                Url = neverListedSource.Url,
                FullListedAt = null, // null = infinite delay
                ConfigFingerprint = null,
            };

            // Input order: NeverListed first (both URL-first and input-first — maximally hostile).
            var result = ListingSelector.Select(
                [neverListedSource, configChangedSource],
                KnownOf(neverRecord, changedRecord),
                Config(listingsPerPass: 1),
                Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.ConfigurationChanged, result[0].Reason);
            Assert.Equal(configChangedSource.Url, result[0].Source.Url);
        }

        // ── Disabled sources are never selected ───────────────────────────────────────────────

        [Fact]
        public void DisabledSource_IsNotSelected_EvenWhenFingerprintDiffers()
        {
            // An Enabled=false source is deliberately excluded regardless of its state. Its
            // configuration change will be seen on re-activation, which is itself a fingerprint
            // change, so nothing is silently lost.
            var source = Source(enabled: false);
            var record = UpToDateRecord(source);
            record.ConfigFingerprint = "stale"; // would be ConfigurationChanged if enabled

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Empty(result);
        }

        // ── Source absent from known is treated as NeverListed ────────────────────────────────

        [Fact]
        public void SourceAbsentFromKnown_IsTreatedAsNeverListed()
        {
            // A source the database has no row for is a brand-new source. There is no clock to
            // read, so it is treated exactly like a source whose FullListedAt is null.
            var source = Source();

            var result = ListingSelector.Select([source], KnownOf(), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.NeverListed, result[0].Reason);
            Assert.True(result[0].Full);
        }

        // ── Deterministic ordering under identical clocks ─────────────────────────────────────

        [Fact]
        public void DeterministicOrdering_IsIndependentOfInputOrder()
        {
            // On a first fill every source has null clocks and identical delay. Without a stable
            // tiebreaker the two turns of the same pass would diverge, making the work
            // non-reproducible in production and non-verifiable in tests.
            var sourceA = Source("https://z.test/channel"); // sorts last by URL
            var sourceB = Source("https://a.test/channel"); // sorts first by URL

            // Both NeverListed, both with null FullListedAt.
            var recordA = new SourceRecord { Url = sourceA.Url, FullListedAt = null };
            var recordB = new SourceRecord { Url = sourceB.Url, FullListedAt = null };

            // Give the sources in opposite orders and verify the result is identical both times.
            var resultForward = ListingSelector.Select(
                [sourceA, sourceB],
                KnownOf(recordA, recordB),
                Config(),
                Now);

            var resultReverse = ListingSelector.Select(
                [sourceB, sourceA],
                KnownOf(recordA, recordB),
                Config(),
                Now);

            Assert.Equal(
                resultForward.Select(r => r.Source.Url),
                resultReverse.Select(r => r.Source.Url));
        }

        // ── ShortListedAt null with a recent full listing is a short listing ─────────────────

        [Fact]
        public void NullShortListedAt_WithRecentFullListing_IsDueAsShort()
        {
            // In practice a completed full listing stamps both clocks. This scenario can only arise
            // from a hand-edited database, but the function must not crash or produce a full
            // listing: FullListedAt is set and recent, so FullListingDue and NeverListed do not
            // apply, and a null ShortListedAt still satisfies ShortListingDue.
            var source = Source();
            var record = UpToDateRecord(source);
            record.FullListedAt = Now.AddHours(-1); // recent full listing
            record.ShortListedAt = null;            // never short-listed (hand-edited)

            var result = ListingSelector.Select([source], KnownOf(record), Config(), Now);

            Assert.Single(result);
            Assert.Equal(ListingReason.ShortListingDue, result[0].Reason);
            Assert.False(result[0].Full);
        }

        // ── Zero or negative ListingsPerPass is safe ─────────────────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ListingsPerPass_ZeroOrNegative_ReturnsEmpty_DoesNotThrow(int listingsPerPass)
        {
            // The cap is a throughput setting, not a minimum-batch guarantee. A zero or negative
            // value means the administrator has paused the listing step; the function must not crash
            // or behave unexpectedly.
            var source = Source();
            var record = new SourceRecord { Url = source.Url, FullListedAt = null };

            var exception = Record.Exception(() =>
                ListingSelector.Select([source], KnownOf(record), Config(listingsPerPass), Now));

            Assert.Null(exception);

            var result = ListingSelector.Select([source], KnownOf(record), Config(listingsPerPass), Now);
            Assert.Empty(result);
        }
    }
}
