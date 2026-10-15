using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class ResolutionTaskTests
    {
        [Fact]
        public void Key_IsVodResolution_AndTheTriggerIsFifteenMinutes()
        {
            // The key is what Jellyfin persists triggers against, so it is part of the contract.
            var task = new ResolutionTask(
                Substitute.For<IVodResolutionRunner>(),
                Substitute.For<ITaskManager>(),
                NullLogger<ResolutionTask>.Instance);

            Assert.Equal("VodResolution", task.Key);
            Assert.Equal("VOD Resolution", task.Name);

            var trigger = Assert.Single(task.GetDefaultTriggers());
            Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
            Assert.Equal(TimeSpan.FromMinutes(15).Ticks, trigger.IntervalTicks);
        }

        [Fact]
        public async Task ExecuteAsync_LaunchesOneWavePerPlatform_AndSurvivesOneThatThrows()
        {
            // A platform in backoff, or one whose extractor broke, must not stop the others: the
            // throttle gives each its own lane precisely so they are independent.
            var runner = Substitute.For<IVodResolutionRunner>();
            runner.PlatformsWithWork(Arg.Any<PluginConfiguration>())
                  .Returns(["Youtube", "ArteTV"], []);
            runner.ResolveWaveAsync(Arg.Any<PluginConfiguration>(), "Youtube", Arg.Any<CancellationToken>())
                  .Returns<Task<int>>(_ => throw new InvalidOperationException("extractor broke"));
            runner.ResolveWaveAsync(Arg.Any<PluginConfiguration>(), "ArteTV", Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(3));

            var task = new ResolutionTask(
                runner, Substitute.For<ITaskManager>(), NullLogger<ResolutionTask>.Instance);

            // No exception escapes: a failed pass would write an activity-log entry, once per wave.
            await task.RunPassAsync(new PluginConfiguration(), new Progress<double>(), CancellationToken.None);

            await runner.Received(1).ResolveWaveAsync(
                Arg.Any<PluginConfiguration>(), "ArteTV", Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ExecuteAsync_RequeuesItself_WhileWorkRemains()
        {
            var runner = Substitute.For<IVodResolutionRunner>();
            // Work before the wave, work still after it.
            runner.PlatformsWithWork(Arg.Any<PluginConfiguration>()).Returns(["Youtube"]);
            runner.ResolveWaveAsync(Arg.Any<PluginConfiguration>(), "Youtube", Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(20));
            var tasks = Substitute.For<ITaskManager>();

            await new ResolutionTask(runner, tasks, NullLogger<ResolutionTask>.Instance)
                .RunPassAsync(new PluginConfiguration(), new Progress<double>(), CancellationToken.None);

            // QueueScheduledTask, not QueueIfNotRunning: from inside the task the latter sees its
            // own Running state and enqueues nothing.
            tasks.Received(1).QueueScheduledTask<ResolutionTask>();
        }

        [Fact]
        public async Task ExecuteAsync_DoesNotRequeue_WhenNoVideoWasResolved()
        {
            // B-C2: a pass that resolved nothing made no forward progress. It must not trigger
            // another immediate execution — the 15-minute interval is the right pacing for that case.
            // This covers the hot-loop scenario where the only candidates are unconfigured sources
            // (the wave stamps them and returns 0).
            var runner = Substitute.For<IVodResolutionRunner>();
            runner.PlatformsWithWork(Arg.Any<PluginConfiguration>()).Returns(["Youtube"]);
            runner.ResolveWaveAsync(Arg.Any<PluginConfiguration>(), "Youtube", Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(0));
            var tasks = Substitute.For<ITaskManager>();

            await new ResolutionTask(runner, tasks, NullLogger<ResolutionTask>.Instance)
                .RunPassAsync(new PluginConfiguration(), new Progress<double>(), CancellationToken.None);

            tasks.DidNotReceive().QueueScheduledTask<ResolutionTask>();
        }

        [Fact]
        public async Task RunPassAsync_CompletesNormally_WhenPlatformsWithWorkThrows()
        {
            // A SQLite lock, a permission error, or a corrupt file on PlatformsWithWork must not
            // produce a failed task execution — Jellyfin logs an activity-log entry for every
            // failed execution, and at one per wave this would fill the user's log with alarms
            // about work that is simply retried.
            var runner = Substitute.For<IVodResolutionRunner>();
            runner.PlatformsWithWork(Arg.Any<PluginConfiguration>())
                  .Returns<IReadOnlyList<string>>(_ => throw new InvalidOperationException("database locked"));

            var task = new ResolutionTask(
                runner, Substitute.For<ITaskManager>(), NullLogger<ResolutionTask>.Instance);

            // Must complete without throwing.
            await task.RunPassAsync(new PluginConfiguration(), new Progress<double>(), CancellationToken.None);
        }

        [Fact]
        public async Task RunPassAsync_PropagatesCancellation_WhenPlatformsWithWorkThrowsOce()
        {
            // Cancellation is the one case that must propagate: it is not a failure, and swallowing
            // it would leave the task running after Jellyfin asked it to stop.
            var runner = Substitute.For<IVodResolutionRunner>();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            runner.PlatformsWithWork(Arg.Any<PluginConfiguration>())
                  .Returns<IReadOnlyList<string>>(_ => throw new OperationCanceledException(cts.Token));

            var task = new ResolutionTask(
                runner, Substitute.For<ITaskManager>(), NullLogger<ResolutionTask>.Instance);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => task.RunPassAsync(new PluginConfiguration(), new Progress<double>(), cts.Token));
        }
    }
}
