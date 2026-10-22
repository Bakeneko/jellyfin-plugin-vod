using System;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;

namespace Jellyfin.Plugin.VOD.Services.Publication
{
    /// <summary>
    /// What a classified outcome carries: the named cause, a plain-text detail for the
    /// configuration page, and whether the failure counts against the video's retry backoff.
    /// </summary>
    /// <param name="Outcome">The named cause.</param>
    /// <param name="Detail">
    /// The first line of stderr, the <c>availability</c> value, or a synthetic explanation.
    /// Null on <see cref="CallOutcome.Ok"/> and <see cref="CallOutcome.ParseError"/>.
    /// </param>
    /// <param name="CountsAgainstTheVideo">
    /// Whether this failure nudges the video one step further back in its retry schedule.
    /// False for platform-level causes (<see cref="CallOutcome.RateLimited"/>,
    /// <see cref="CallOutcome.Timeout"/>): a rate limit or a silent session drop is not the
    /// video's fault. True for everything else. Success resets the counter to zero regardless.
    /// </param>
    public sealed record ClassifiedOutcome(
        CallOutcome Outcome,
        string? Detail,
        bool CountsAgainstTheVideo);

    /// <summary>
    /// Translates a <see cref="MetadataResult"/> into a named cause and a human-readable detail.
    /// </summary>
    /// <remarks>
    /// Pure function — no state, no I/O, no side effects. It exists because the previous model
    /// recorded only the word "Failed", which made fourteen of sixteen stuck videos on the test
    /// instance undiagnosable without a manual investigation through the database and 230 MB of
    /// logs. Every name below was reachable at that moment; none was recorded.
    /// </remarks>
    public static class OutcomeClassifier
    {
        /// <summary>Maximum length of a detail string stored here.</summary>
        /// <remarks>
        /// The database column already caps at 500. Matching it here avoids silent truncation at
        /// the write layer, which would discard characters without any indication.
        /// </remarks>
        private const int MaxDetailLength = 500;

        /// <summary>
        /// Detail used when a <see cref="MetadataOutcome.ChannelOffAir"/> result arrives here.
        /// </summary>
        /// <remarks>
        /// ChannelOffAir is produced by the live probe, which consumes it itself and never calls
        /// this classifier. Receiving it here means a channel URL was treated as a video URL.
        /// Naming that honestly is more useful than any value from stderr.
        /// </remarks>
        private const string ChannelOffAirDetail =
            "Platform answered about a channel, not a video — a channel URL may have been used where a video URL was expected.";

        /// <summary>
        /// Classifies a metadata result into a named cause.
        /// </summary>
        /// <param name="result">The result produced by <c>YtDlpService.Interpret</c>.</param>
        /// <returns>The named cause, a detail, and whether this failure is counted against the video.</returns>
        public static ClassifiedOutcome Classify(MetadataResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            // ChannelOffAir is the live probe's signal; the probe consumes it and does not call
            // this classifier. If it arrives, the most likely cause is a channel URL standing in
            // for a video URL — name that rather than propagating a confusing outcome.
            if (result.Outcome == MetadataOutcome.ChannelOffAir)
                return new ClassifiedOutcome(CallOutcome.ExtractorError, ChannelOffAirDetail, CountsAgainstTheVideo: true);

            // Normal success: metadata arrived and the video is playable.
            // The IsUnavailable guard is defensive: Interpret already reclassifies unavailable
            // metadata as Failed, so Succeeded+IsUnavailable cannot arrive in normal operation.
            // If it does, fall through to the failure path rather than publishing a broken item.
            if (result.Outcome == MetadataOutcome.Succeeded
                && !(result.Metadata?.IsUnavailable ?? false))
                return new ClassifiedOutcome(CallOutcome.Ok, null, CountsAgainstTheVideo: false);

            // ── Failure path — decision order is explicit and must not be reordered ──────────

            // 1. TimedOut wins over everything, including a descriptive stderr. Measured: a
            //    rate-limited session stops responding rather than reporting an error, so the
            //    timeout signal is the most informative piece of information the service has.
            if (result.TimedOut)
                return new ClassifiedOutcome(CallOutcome.Timeout, FirstLine(result.StdErr), CountsAgainstTheVideo: false);

            // 2. Metadata.Availability when yt-dlp provides it. A structured enum value that
            //    outlives prose rewrites between yt-dlp versions; it wins over a contradictory
            //    stderr. Null and unrecognised values fall through to the stderr path.
            if (result.Metadata?.Availability is { } availability)
            {
                var fromAvailability = ClassifyAvailability(availability);
                if (fromAvailability is not null)
                    return fromAvailability;
            }

            // 3. First line of stderr.
            // This table is fragile by nature: it is prose from yt-dlp and its wording changes
            // between versions. Prefer Availability when present — that is why the decision order
            // above matters.
            return ClassifyStdErr(result.StdErr);
        }

        /// <summary>
        /// Maps yt-dlp's <c>availability</c> field to a <see cref="CallOutcome"/>.
        /// Returns null for values that do not decide the outcome (fall through to stderr).
        /// </summary>
        private static ClassifiedOutcome? ClassifyAvailability(string availability)
        {
            // Values that establish a cause directly. "public" and "unlisted" are normal and do
            // not decide; any future yt-dlp value falls through to stderr rather than being mapped
            // to the wrong cause.
            return availability switch
            {
                "needs_auth" or "subscriber_only" or "premium_only"
                    => new ClassifiedOutcome(CallOutcome.NeedsAuth, availability, CountsAgainstTheVideo: true),

                "private"
                    => new ClassifiedOutcome(CallOutcome.Unavailable, availability, CountsAgainstTheVideo: true),

                // "public", "unlisted", or anything yt-dlp invents later: do not decide.
                _ => null,
            };
        }

        /// <summary>
        /// Maps the first line of stderr to a <see cref="CallOutcome"/>.
        /// </summary>
        /// <remarks>
        /// All comparisons use <see cref="StringComparison.OrdinalIgnoreCase"/> — culture-invariant
        /// and case-insensitive, so the Turkish dotted-I cannot break a match. Unrecognised output
        /// always falls to <see cref="CallOutcome.ExtractorError"/> with the raw detail preserved:
        /// a silent default is exactly what this class was built to replace.
        /// </remarks>
        private static ClassifiedOutcome ClassifyStdErr(string? stderr)
        {
            var first = FirstLine(stderr);

            if (first is null)
            {
                // No stderr at all: exit 0 with output that did not parse as JSON. This is the only
                // failure path that produces no stderr, which is how it is told apart from other
                // failures whose stderr we simply do not recognise.
                return new ClassifiedOutcome(CallOutcome.ParseError, null, CountsAgainstTheVideo: true);
            }

            // The patterns below are yt-dlp prose — fragile by nature (see class remarks).
            // RateLimited is checked FIRST: its markers are unambiguous, and it is the only
            // outcome whose misclassification has a cost beyond a wrong label — it must never
            // advance the retry backoff. Placing it first makes that cost unreachable by a
            // future wording collision rather than merely absent today.

            if (ContainsOrdinalIgnoreCase(first, "HTTP Error 429")
                || ContainsOrdinalIgnoreCase(first, "Too Many Requests")
                || ContainsOrdinalIgnoreCase(first, "rate-limit"))
                return new ClassifiedOutcome(CallOutcome.RateLimited, first, CountsAgainstTheVideo: false);

            // NeedsAuth: use full phrases, not substrings. The original single-word "age" matched
            // "webpage", "language", "image" — words common in unrelated yt-dlp errors.
            // "ERROR: Unable to download webpage: HTTP Error 429" contains "webpage" → "age",
            // which would have classified a rate-limit as NeedsAuth and incorrectly advanced
            // every video's retry backoff in a rate-limited pass.
            if (ContainsOrdinalIgnoreCase(first, "Sign in")
                || ContainsOrdinalIgnoreCase(first, "confirm your age")
                || ContainsOrdinalIgnoreCase(first, "age-restricted")
                || ContainsOrdinalIgnoreCase(first, "age restricted")
                || ContainsOrdinalIgnoreCase(first, "inappropriate for some users")
                || ContainsOrdinalIgnoreCase(first, "members-only")
                || ContainsOrdinalIgnoreCase(first, "confirm you're not a bot"))
                return new ClassifiedOutcome(CallOutcome.NeedsAuth, first, CountsAgainstTheVideo: true);

            // GeoBlocked: tightened from the bare three-character "geo" to full phrases.
            if (ContainsOrdinalIgnoreCase(first, "not available in your country")
                || ContainsOrdinalIgnoreCase(first, "geo-restricted")
                || ContainsOrdinalIgnoreCase(first, "geo restriction")
                || ContainsOrdinalIgnoreCase(first, "geoblocked")
                || ContainsOrdinalIgnoreCase(first, "blocked in"))
                return new ClassifiedOutcome(CallOutcome.GeoBlocked, first, CountsAgainstTheVideo: true);

            if (ContainsOrdinalIgnoreCase(first, "Video unavailable")
                || ContainsOrdinalIgnoreCase(first, "Private video")
                || ContainsOrdinalIgnoreCase(first, "has been removed")
                || ContainsOrdinalIgnoreCase(first, "This video is not available"))
                return new ClassifiedOutcome(CallOutcome.Unavailable, first, CountsAgainstTheVideo: true);

            if (ContainsOrdinalIgnoreCase(first, "Requested format is not available")
                || ContainsOrdinalIgnoreCase(first, "No video formats found"))
                return new ClassifiedOutcome(CallOutcome.NoFormats, first, CountsAgainstTheVideo: true);

            if (ContainsOrdinalIgnoreCase(first, "Unable to download")
                || ContainsOrdinalIgnoreCase(first, "Connection")
                || ContainsOrdinalIgnoreCase(first, "urlopen error")
                || ContainsOrdinalIgnoreCase(first, "Temporary failure in name resolution"))
                return new ClassifiedOutcome(CallOutcome.NetworkError, first, CountsAgainstTheVideo: true);

            // Unrecognised output: ExtractorError with the raw detail. A silent default here
            // would reproduce the original bug — one unknown error silently losing its cause.
            return new ClassifiedOutcome(CallOutcome.ExtractorError, first, CountsAgainstTheVideo: true);
        }

        /// <summary>
        /// Returns the first line of <paramref name="text"/>, truncated to
        /// <see cref="MaxDetailLength"/>, or null when the input is null or empty.
        /// </summary>
        /// <remarks>
        /// The first line carries the ERROR: message. The rest is a stack trace that adds length
        /// but not meaning at the point of display.
        /// </remarks>
        private static string? FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var newline = text.IndexOfAny(['\n', '\r']);
            var line = newline >= 0 ? text[..newline] : text;

            return line.Length > MaxDetailLength ? line[..MaxDetailLength] : line;
        }

        private static bool ContainsOrdinalIgnoreCase(string source, string value)
            => source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}
