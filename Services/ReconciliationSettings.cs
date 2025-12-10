using System;

namespace MeTubeServer.Services;

public class ReconciliationSettings
{
    public TimeSpan MaxDetectionLag { get; init; } = TimeSpan.FromDays(7);
    public double CadenceMultiplier { get; init; } = 1.0;
    public ActivityBucketIntervals Hot { get; init; } = new(TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromDays(7));
    public ActivityBucketIntervals Warm { get; init; } = new(TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7));
    public ActivityBucketIntervals Cold { get; init; } = new(TimeSpan.FromDays(7), TimeSpan.FromDays(14), TimeSpan.FromDays(60));
    public ActivityBucketIntervals Frozen { get; init; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(60), TimeSpan.FromDays(60));
    public TimeSpan HotThreshold { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan WarmThreshold { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ColdThreshold { get; init; } = TimeSpan.FromDays(180);
    public TimeSpan WebSubReliabilityWindow { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan WebSubColdChannelThreshold { get; init; } = TimeSpan.FromDays(30);
}

public record ActivityBucketIntervals(TimeSpan Min, TimeSpan Max, TimeSpan BackoffMax);
