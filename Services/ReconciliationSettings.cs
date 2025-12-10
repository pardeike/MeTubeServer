using System;

namespace MeTubeServer.Services;

public class ReconciliationSettings
{
    public TimeSpan MaxDetectionLag { get; init; } = TimeSpan.FromDays(7);
}
