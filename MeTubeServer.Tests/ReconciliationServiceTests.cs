using MeTubeServer.Models;
using MeTubeServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeTubeServer.Tests;

public class ReconciliationServiceTests
{
    [Fact]
    public void DetermineActivityBucket_UsesThresholds()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(ActivityBucket.Hot, service.DetermineActivityBucket(now - TimeSpan.FromDays(1), now));
        Assert.Equal(ActivityBucket.Warm, service.DetermineActivityBucket(now - TimeSpan.FromDays(10), now));
        Assert.Equal(ActivityBucket.Cold, service.DetermineActivityBucket(now - TimeSpan.FromDays(60), now));
        Assert.Equal(ActivityBucket.Frozen, service.DetermineActivityBucket(now - TimeSpan.FromDays(200), now));
        Assert.Equal(ActivityBucket.Frozen, service.DetermineActivityBucket(null, now));
    }

    [Fact]
    public void ComputeBaseInterval_ClampsPerBucket()
    {
        var service = CreateService();

        var hotBase = service.ComputeBaseInterval(TimeSpan.FromDays(4), ActivityBucket.Hot);
        Assert.True(hotBase >= TimeSpan.FromHours(6) && hotBase <= TimeSpan.FromHours(24));

        var coldBase = service.ComputeBaseInterval(TimeSpan.FromDays(40), ActivityBucket.Cold);
        Assert.InRange(coldBase, TimeSpan.FromDays(7), TimeSpan.FromDays(14));

        var frozenBase = service.ComputeBaseInterval(TimeSpan.FromDays(90), ActivityBucket.Frozen);
        Assert.InRange(frozenBase, TimeSpan.FromDays(30), TimeSpan.FromDays(60));
    }

    [Fact]
    public void CalculateAdaptiveInterval_RespectsBucketCeilings()
    {
        var service = CreateService();

        var hotInterval = service.CalculateAdaptiveInterval(TimeSpan.FromDays(1), ActivityBucket.Hot, 4);
        Assert.True(hotInterval <= TimeSpan.FromDays(7));

        var frozenInterval = service.CalculateAdaptiveInterval(TimeSpan.FromDays(45), ActivityBucket.Frozen, 4);
        Assert.Equal(TimeSpan.FromDays(60), frozenInterval);
    }

    [Fact]
    public void ShouldReconcileChannel_HonorsDetectionLag()
    {
        var service = CreateService();
        var lastChecked = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        var state = new ReconciliationService.ChannelReconcileState(lastChecked, TimeSpan.FromDays(30), 0, TimeSpan.FromDays(2));
        var cadence = new ReconciliationService.ChannelCadence(TimeSpan.FromDays(2), TimeSpan.FromDays(2), lastChecked);

        var now = lastChecked + TimeSpan.FromDays(5);
        var shouldRun = service.ShouldReconcileChannel(new Channel(), state, cadence, now, out var wait, out var reason);

        Assert.False(shouldRun);
        Assert.Equal(ReconcileDecisionReason.NotYetDue, reason);
        Assert.Equal(TimeSpan.FromDays(2), wait);

        now = lastChecked + TimeSpan.FromDays(8);
        shouldRun = service.ShouldReconcileChannel(new Channel(), state, cadence, now, out wait, out reason);

        Assert.True(shouldRun);
        Assert.Equal(ReconcileDecisionReason.DueByCadence, reason);
        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public void ShouldReconcileChannel_SkipsWhenWebSubReliable()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;
        var channel = new Channel
        {
            LastWebSubNotification = now - TimeSpan.FromDays(2)
        };
        var state = new ReconciliationService.ChannelReconcileState(DateTimeOffset.MinValue, TimeSpan.FromHours(12), 0, TimeSpan.FromHours(12));
        var cadence = new ReconciliationService.ChannelCadence(TimeSpan.FromHours(12), TimeSpan.FromHours(12), now - TimeSpan.FromDays(3));

        var shouldRun = service.ShouldReconcileChannel(channel, state, cadence, now, out _, out var reason);

        Assert.False(shouldRun);
        Assert.Equal(ReconcileDecisionReason.WebSubReliable, reason);
    }

    [Fact]
    public void ShouldReconcileChannel_RunsFirstTimeForFrozen()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;
        var channel = new Channel();
        var cadence = new ReconciliationService.ChannelCadence(TimeSpan.FromDays(60), null, null);
        var state = new ReconciliationService.ChannelReconcileState(DateTimeOffset.MinValue, TimeSpan.FromDays(30), 0, cadence.CadenceEma);

        var shouldRun = service.ShouldReconcileChannel(channel, state, cadence, now, out var wait, out var reason);

        Assert.True(shouldRun);
        Assert.Equal(ReconcileDecisionReason.FirstRun, reason);
        Assert.Equal(TimeSpan.Zero, wait);
    }

    private static ReconciliationService CreateService(ReconciliationSettings? settings = null)
    {
        return new ReconciliationService(
            youtubeApi: null!,
            taskQueue: new FakeBackgroundTaskQueue(),
            logger: NullLogger<ReconciliationService>.Instance,
            settings: Options.Create(settings ?? new ReconciliationSettings()));
    }

    private sealed class FakeBackgroundTaskQueue : IBackgroundTaskQueue
    {
        public ValueTask QueueBackgroundWorkItemAsync(Func<IServiceProvider, CancellationToken, ValueTask> workItem) => ValueTask.CompletedTask;

        public ValueTask<Func<IServiceProvider, CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<Func<IServiceProvider, CancellationToken, ValueTask>>((_, _) => ValueTask.CompletedTask);
        }
    }
}
