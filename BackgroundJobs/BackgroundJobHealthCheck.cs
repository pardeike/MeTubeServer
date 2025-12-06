using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MeTubeServer.BackgroundJobs;

/// <summary>
/// Health check for monitoring background job health.
/// Tracks last execution time and errors for background jobs.
/// </summary>
public class BackgroundJobHealthCheck : IHealthCheck
{
    private readonly Dictionary<string, JobHealthStatus> _jobStatuses = new();
    private readonly object _lock = new();

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
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
    }

    /// <summary>
    /// Records a successful job execution.
    /// </summary>
    public void RecordJobExecution(string jobName, TimeSpan expectedInterval)
    {
        lock (_lock)
        {
            if (!_jobStatuses.ContainsKey(jobName))
            {
                _jobStatuses[jobName] = new JobHealthStatus
                {
                    ExpectedInterval = expectedInterval
                };
            }

            _jobStatuses[jobName].LastRunTime = DateTimeOffset.UtcNow;
            _jobStatuses[jobName].ConsecutiveErrors = 0;
        }
    }

    /// <summary>
    /// Records a job execution error.
    /// </summary>
    public void RecordJobError(string jobName)
    {
        lock (_lock)
        {
            if (_jobStatuses.ContainsKey(jobName))
            {
                _jobStatuses[jobName].ConsecutiveErrors++;
            }
        }
    }

    private class JobHealthStatus
    {
        public DateTimeOffset LastRunTime { get; set; } = DateTimeOffset.UtcNow;
        public int ConsecutiveErrors { get; set; }
        public TimeSpan ExpectedInterval { get; set; }
    }
}
