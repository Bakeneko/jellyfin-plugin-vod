using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>The reason a source was selected for listing in this turn of the pass.</summary>
    /// <remarks>
    /// Carried on each <see cref="PlannedListing"/> so that the pass can act on it without
    /// recomputing: only <see cref="ConfigurationChanged"/> clears <c>FullListedAt</c>, logs report
    /// the reason so the operator can trace why a source moved, and the sort order depends on this
    /// value.
    /// </remarks>
    public enum ListingReason
    {
        /// <summary>
        /// The computed fingerprint of the source's configuration disagrees with the stored one.
        /// The pass will clear <c>FullListedAt</c> on this source so that the checkup can run.
        /// </summary>
        ConfigurationChanged,

        /// <summary>
        /// <c>FullListedAt</c> is null: no listing has ever completed for this source. The first
        /// listing is always full — on an empty source every short entry would escalate to a full
        /// anyway, so paying for the full directly avoids a wasted round-trip.
        /// </summary>
        NeverListed,

        /// <summary>
        /// The last full listing completed more than <c>FullListingIntervalDays</c> ago. Only a
        /// full listing detects a deletion or a retitle below the head of the listing.
        /// </summary>
        FullListingDue,

        /// <summary>
        /// The last short listing completed more than <c>ListingIntervalMinutes</c> ago (or never
        /// completed). This is the only reason that produces a short listing.
        /// </summary>
        ShortListingDue,
    }

    /// <summary>
    /// A source that has been selected for listing in this turn, with the kind and reason.
    /// </summary>
    public sealed record PlannedListing(SourceDefinition Source, bool Full, ListingReason Reason);

    /// <summary>
    /// Decides which sources to list in one turn of the listing pass, and whether each listing is
    /// full or short.
    /// </summary>
    /// <remarks>
    /// Pure function: it reads the state it is given and writes nothing. The pass that calls it
    /// then drives the selected listings and updates the clocks.
    /// <para>
    /// This selection is done in C# rather than SQL because it crosses the database state with the
    /// per-source <see cref="SourceDefinition.ListingIntervalMinutes"/>, which lives in Jellyfin's
    /// XML configuration and not in the table. A query that received every interval as a parameter
    /// would gain nothing over this approach — the number of sources is on the order of ten.
    /// </para>
    /// </remarks>
    public static class ListingSelector
    {
        /// <summary>
        /// Returns the sources to list in this turn, in priority order, capped at
        /// <see cref="PluginConfiguration.ListingsPerPass"/>.
        /// </summary>
        /// <param name="configured">The sources the administrator has configured.</param>
        /// <param name="known">
        /// The database state, keyed by source URL, case-insensitive (matching the database
        /// collation).
        /// </param>
        /// <param name="config">
        /// The plugin configuration that carries the global scheduling parameters.
        /// </param>
        /// <param name="now">The current time, used to evaluate all clock comparisons.</param>
        /// <returns>
        /// At most <see cref="PluginConfiguration.ListingsPerPass"/> planned listings, sorted by
        /// reason priority, then by delay (most overdue first; a null clock counts as infinite
        /// delay), then by URL for deterministic ordering when delays are equal.
        /// </returns>
        public static IReadOnlyList<PlannedListing> Select(
            IReadOnlyList<SourceDefinition> configured,
            IReadOnlyDictionary<string, SourceRecord> known,
            PluginConfiguration config,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(configured);
            ArgumentNullException.ThrowIfNull(known);
            ArgumentNullException.ThrowIfNull(config);

            // A zero or negative cap means "list nothing this turn". ListingsPerPass is a
            // throughput throttle, not a minimum-batch guarantee: an administrator who sets it to
            // zero intends to pause the listing step.
            if (config.ListingsPerPass <= 0)
                return [];

            var candidates = new List<(PlannedListing Listing, DateTimeOffset? DelayedSince)>(configured.Count);

            foreach (var source in configured)
            {
                // Disabled sources are never selected, even when their configuration changed. A
                // re-activation is itself a configuration change (Enabled enters the fingerprint),
                // so the mismatch will be seen then and nothing is lost by skipping now.
                if (!source.Enabled)
                    continue;

                known.TryGetValue(source.Url, out var record);

                var reason = DetermineReason(source, record, config, now);
                if (reason is null)
                    continue;

                // Full for every reason except ShortListingDue. A first listing (NeverListed) is
                // always full: on an empty source every short entry would be unknown and would
                // escalate to a full anyway, so paying for the full directly avoids a wasted call.
                var full = reason.Value != ListingReason.ShortListingDue;

                // The clock that made this source due. Full reasons use FullListedAt; the short
                // reason uses ShortListedAt. Null means the clock has never been set, which is
                // treated as infinite delay (most overdue) during sorting.
                var delayedSince = reason.Value == ListingReason.ShortListingDue
                    ? record?.ShortListedAt
                    : record?.FullListedAt;

                candidates.Add((new PlannedListing(source, full, reason.Value), delayedSince));
            }

            return candidates
                // Reason priority: ConfigurationChanged (0) before NeverListed (1) before
                // FullListingDue (2) before ShortListingDue (3). ConfigurationChanged sorts
                // first because ~300 sources can be NeverListed on a fresh install: without this
                // priority, an administrator's change would wait tens of turns before taking effect,
                // while they watch the UI and see nothing happen.
                .OrderBy(c => (int)c.Listing.Reason)
                // Within a reason, most overdue first. Null (never set) maps to MinValue so it
                // sorts ahead of any real timestamp.
                .ThenBy(c => c.DelayedSince ?? DateTimeOffset.MinValue)
                // URL as a final tiebreaker: two sources with identical clocks — all null on a
                // first fill — would otherwise be ordered by enumeration, making the order
                // non-reproducible between runs and between tests.
                .ThenBy(c => c.Listing.Source.Url, StringComparer.Ordinal)
                .Take(config.ListingsPerPass)
                .Select(c => c.Listing)
                .ToList();
        }

        private static ListingReason? DetermineReason(
            SourceDefinition source,
            SourceRecord? record,
            PluginConfiguration config,
            DateTimeOffset now)
        {
            // ConfigurationChanged: the computed fingerprint disagrees with what the last
            // successful checkup stored. A null stored fingerprint means no checkup has ever run;
            // in that case the source is already due via NeverListed, and reporting it as
            // ConfigurationChanged would produce false log entries throughout the entire initial
            // fill (~300 sources with no fingerprint yet).
            if (record?.ConfigFingerprint is not null &&
                record.ConfigFingerprint != SourceFingerprint.Of(source))
            {
                return ListingReason.ConfigurationChanged;
            }

            // NeverListed: FullListedAt null means no listing has ever completed. A first listing
            // is always full — on an empty source every short entry would be unknown and would
            // escalate to a full anyway, so paying directly avoids a wasted round-trip.
            if (record is null || record.FullListedAt is null)
                return ListingReason.NeverListed;

            // FullListingDue: the last full listing is older than the configured interval. Only a
            // full listing can detect a deletion or a retitle below the head of the listing;
            // a short listing sees only the most recent entries.
            if (now - record.FullListedAt.Value > TimeSpan.FromDays(config.FullListingIntervalDays))
                return ListingReason.FullListingDue;

            // ShortListingDue: the short clock is null or has exceeded this source's own
            // interval. The interval is per-source so that a source polled every 5 minutes and
            // one polled every 1440 minutes do not compete on equal footing.
            if (record.ShortListedAt is null ||
                now - record.ShortListedAt.Value > TimeSpan.FromMinutes(source.ListingIntervalMinutes))
            {
                return ListingReason.ShortListingDue;
            }

            return null;
        }
    }
}
