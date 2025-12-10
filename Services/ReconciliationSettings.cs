using System;

namespace MeTubeServer.Services;

public class ReconciliationSettings
{
    public TimeSpan MaxDetectionLag { get; init; } = TimeSpan.FromDays(7);
    public double CadenceMultiplier { get; init; } = 1.0;
    public TimeSpan BaseIntervalMin { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan BaseIntervalMax { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan MaxAdaptiveInterval { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan HotThreshold { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan WarmThreshold { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ColdThreshold { get; init; } = TimeSpan.FromDays(180);
    public TimeSpan WebSubReliabilityWindow { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan WebSubColdChannelThreshold { get; init; } = TimeSpan.FromDays(30);
}
