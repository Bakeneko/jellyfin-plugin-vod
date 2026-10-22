using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Publication
{
    /// <summary>
    /// The scheduled task. It holds no rule of its own, so what is worth pinning is the wiring:
    /// that it actually runs the pass, that the arguments it hands over are the live ones, and that
    /// it does not swallow a cancellation.
    /// </summary>
    public sealed class PublicationTaskTests
    {
        private static readonly PassReport Nothing = new(1, 0, 0, 0, 0, [], false);

        private readonly IPublicationPass _pass = Substitute.For<IPublicationPass>();
        private readonly RecordingLogger<PublicationTask> _log = new();

        private PluginConfiguration? _config = new();

        private PublicationTask Task() => new(_pass, () => _config, _log);

        private static PluginConfiguration ConfigWith(string sourceUrl)
        {
            var config = new PluginConfiguration();
            config.Sources.Add(new SourceDefinition { Url = sourceUrl, Enabled = true });
            return config;
        }

        // ── It runs the pass ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheTask_RunsThePass_AndHandsItTheProgressReporterItWasGiven()
        {
            // Both halves matter. "Runs the pass" catches a task registered but wired to nothing —
            // which is exactly the failure this whole task exists to avoid. The progress reporter
            // is the pass's only liveness signal, and a first fill is hours long: a task that
            // reported progress itself, or dropped it, would leave an administrator unable to tell
            // a working pass from a stuck one.
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Nothing);

            var reported = new List<double>();
            var progress = new DelegateProgress(reported.Add);

            await Task().ExecuteAsync(progress, CancellationToken.None);

            await _pass.Received(1).RunPassAsync(
                _config!, progress, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task TheConfiguration_IsReadAtEveryRun_NotCapturedAtConstruction()
        {
            // A scheduled task is a singleton that outlives any number of administrator edits. A
            // configuration captured when the server started would make every edit invisible until
            // a restart — and the pass's own detection of a configuration change, which is what
            // makes an edit take effect, would never see anything to detect.
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Nothing);

            var task = Task();

            var first = ConfigWith("https://example.test/first");
            _config = first;
            await task.ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None);

            var second = ConfigWith("https://example.test/second");
            _config = second;
            await task.ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None);

            await _pass.Received(1).RunPassAsync(
                first, Arg.Any<IProgress<double>?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            await _pass.Received(1).RunPassAsync(
                second, Arg.Any<IProgress<double>?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task TheCancellationToken_ReachesThePass()
        {
            // The pass cancels between two sources, never inside one, so the token has to travel
            // all the way down. A task that passed CancellationToken.None would make a Jellyfin
            // shutdown wait for a pass that can run for hours.
            using var cts = new CancellationTokenSource();
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Nothing);

            await Task().ExecuteAsync(new DelegateProgress(_ => { }), cts.Token);

            await _pass.Received(1).RunPassAsync(
                Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                Arg.Any<DateTimeOffset>(), cts.Token);
        }

        [Fact]
        public async Task ACancelledPass_PropagatesInsteadOfReportingSuccess()
        {
            // The pass deliberately does not swallow its own cancellation: the state lives in the
            // database and a source must end up either untouched or entirely worked. A task that
            // caught OperationCanceledException here would report a success Jellyfin would believe,
            // and a half-worked source would stay invisible until the next full listing.
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns<Task<PassReport>>(_ => throw new OperationCanceledException());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task().ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None));
        }

        [Fact]
        public async Task AFailingPass_PropagatesSoJellyfinMarksTheTaskFailed()
        {
            // Nothing above this task can report the failure. Swallowing it would show a green run
            // in the dashboard for a pass that did nothing.
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns<Task<PassReport>>(_ => throw new InvalidOperationException("the database is gone"));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Task().ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None));

            Assert.Equal("the database is gone", thrown.Message);
        }

        // ── No configuration ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task NoConfiguration_RunsNothing_AndSaysSo()
        {
            // Plugin.Instance is null while the plugin is loading, and a scheduled task can be
            // asked to run at any moment. Silence here is what made an operator conclude the task
            // was broken when it had simply not been configured yet.
            _config = null;

            await Task().ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None);

            await _pass.DidNotReceive().RunPassAsync(
                Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            Assert.Contains(
                _log.Entries,
                e => e.Level == LogLevel.Warning
                     && e.Message.Contains("no configuration", StringComparison.OrdinalIgnoreCase));
        }

        // ── A refused lane is only visible here ──────────────────────────────────────────────────

        [Fact]
        public async Task APassThatEndedWithARefusedLane_IsReportedAsIncomplete()
        {
            // The refusal lives in the report and nowhere else: a rate limit and a timeout do not
            // count against a video, so they leave no trace in the database. Without this line an
            // administrator sees a successful run and a library that is still missing videos.
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new PassReport(4, 1, 20, 18, 0, [PlatformLane.Youtube], false));

            await Task().ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None);

            Assert.Contains(
                _log.Entries,
                e => e.Level == LogLevel.Warning
                     && e.Message.Contains(PlatformLane.Youtube, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task APassThatFinishedEverything_LogsNoWarning()
        {
            // Without this, the test above would pass for a task that warns unconditionally.
            _pass.RunPassAsync(
                    Arg.Any<PluginConfiguration>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new PassReport(4, 1, 20, 18, 0, [], false));

            await Task().ExecuteAsync(new DelegateProgress(_ => { }), CancellationToken.None);

            Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Warning);
        }

        // ── Identity and trigger ─────────────────────────────────────────────────────────────────

        [Fact]
        public void TheTask_IsNamedAfterWhatItDoes_AndCarriesAStableKey()
        {
            // The key is what Jellyfin persists triggers against: changing it silently resets an
            // administrator's custom schedule to the default and leaves the old entry orphaned.
            var task = Task();

            Assert.Equal("VodListingAndPublication", task.Key);
            Assert.Equal("VOD Listing & publication", task.Name);
            Assert.Equal("VOD", task.Category);
            Assert.False(string.IsNullOrWhiteSpace(task.Description));
        }

        [Fact]
        public void TheDefaultTrigger_IsASingleShortInterval()
        {
            // Short because an idle pass is one database query per source and no platform call, and
            // the interval is the latency before a new source or a newly listed video is acted on.
            // The background work happens inside one pass, not by waking often.
            var trigger = Assert.Single(Task().GetDefaultTriggers());

            Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
            Assert.NotNull(trigger.IntervalTicks);
            var interval = TimeSpan.FromTicks(trigger.IntervalTicks!.Value);
            Assert.InRange(interval, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
        }

        // ── The seam ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ThePassConcreteType_StillSatisfiesTheSeam()
        {
            // The task is wired to the concrete PublicationService in production; the interface
            // exists for the tests above. This fails the moment the two drift apart.
            Assert.True(typeof(IPublicationPass).IsAssignableFrom(typeof(PublicationService)));
        }

        // ── Doubles ──────────────────────────────────────────────────────────────────────────────

        private sealed class DelegateProgress(Action<double> onReport) : IProgress<double>
        {
            public void Report(double value) => onReport(value);
        }
    }
}
