using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Collections.Concurrent;

namespace MeTubeServer.BackgroundJobs;

/// <summary>
/// Health check for monitoring background job health.
/// Tracks last execution time and errors for background jobs.
/// </summary>
public class BackgroundJobHealthCheck : IHealthCheck
{
    private readonly ConcurrentDictionary<string, JobHealthStatus> _jobStatuses = new();

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var unhealthyJobs = new List<string>();
        var degradedJobs = new List<string>();

        foreach (var (jobName, status) in _jobStatuses)
        {
            // Consider job unhealthy if it hasn't run in 2x its expected interval
            var timeSinceLastRun = now - status.LastRunTime;
            var maxExpectedInterval = status.ExpectedInterval * 2;

            if (timeSinceLastRun > maxExpectedInterval)
            {
                unhealthyJobs.Add($"{jobName} (last run: {status.LastRunTime:O})");
            }
            else if (status.ConsecutiveErrors >= 3)
            {
                degradedJobs.Add($"{jobName} ({status.ConsecutiveErrors} consecutive errors)");
            }
        }

        if (unhealthyJobs.Any())
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Background jobs not running: {string.Join(", ", unhealthyJobs)}"));
        }

        if (degradedJobs.Any())
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Background jobs with errors: {string.Join(", ", degradedJobs)}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy("All background jobs running normally"));
    }

    /// <summary>
    /// Records a successful job execution.
    /// </summary>
    public void RecordJobExecution(string jobName, TimeSpan expectedInterval)
    {
        _jobStatuses.AddOrUpdate(
            jobName,
            new JobHealthStatus
            {
                ExpectedInterval = expectedInterval,
                LastRunTime = DateTimeOffset.UtcNow,
                ConsecutiveErrors = 0
            },
            (key, existing) =>
            {
                existing.LastRunTime = DateTimeOffset.UtcNow;
                existing.ConsecutiveErrors = 0;
                return existing;
            });
    }

    /// <summary>
    /// Records a job execution error.
    /// </summary>
    public void RecordJobError(string jobName)
    {
        _jobStatuses.AddOrUpdate(
            jobName,
            new JobHealthStatus { ConsecutiveErrors = 1 },
            (key, existing) =>
            {
                existing.ConsecutiveErrors++;
                return existing;
            });
    }

    private class JobHealthStatus
    {
        public DateTimeOffset LastRunTime { get; set; } = DateTimeOffset.UtcNow;
        public int ConsecutiveErrors { get; set; }
        public TimeSpan ExpectedInterval { get; set; }
    }
}
