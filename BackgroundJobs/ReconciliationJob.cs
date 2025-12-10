using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MeTubeServer.Data;
using MeTubeServer.Models;
using MeTubeServer.Services;

namespace MeTubeServer.BackgroundJobs;

/// <summary>
/// Background job that periodically reconciles channel videos with adaptive intervals
/// based on channel publishing frequency to reduce YouTube API quota usage.
/// </summary>
public class ReconciliationJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReconciliationJob> _logger;
    private readonly BackgroundJobHealthCheck _healthCheck;
    private readonly IOptions<HubOptions> _options;

    public ReconciliationJob(
        IServiceProvider serviceProvider,
        ILogger<ReconciliationJob> logger,
        BackgroundJobHealthCheck healthCheck,
        IOptions<HubOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _healthCheck = healthCheck;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reconciliation Job started with base interval {Interval} minutes", 
            _options.Value.ReconciliationBaseIntervalMinutes);

        // Wait before first run to let the system stabilize
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var baseInterval = TimeSpan.FromMinutes(_options.Value.ReconciliationBaseIntervalMinutes);
                await ReconcileChannelsAsync(stoppingToken);
                _healthCheck.RecordJobExecution(nameof(ReconciliationJob), baseInterval);
                await Task.Delay(baseInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in reconciliation job");
                _healthCheck.RecordJobError(nameof(ReconciliationJob));
                // Wait a bit before retrying on error
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Reconciliation Job stopped");
    }

    private async Task ReconcileChannelsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
        var reconciliationService = scope.ServiceProvider.GetRequiredService<ReconciliationService>();

        var now = DateTimeOffset.UtcNow;

        // Get all channels that have users subscribed
        var allChannels = await dbContext.Channels
            .Include(c => c.UserChannels)
            .Where(c => c.UserChannels.Any() && c.UploadsPlaylistId != null)
            .ToListAsync(cancellationToken);

        if (allChannels.Count == 0)
        {
            _logger.LogDebug("No channels to reconcile");
            return;
        }

        _logger.LogInformation("Evaluating {Count} channels for reconciliation", allChannels.Count);

        int reconciledCount = 0;
        int skippedCount = 0;
        int webSubSkippedCount = 0;

        foreach (var channel in allChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip reconciliation if WebSub is working reliably for this channel
            if (ShouldSkipDueToWebSub(channel))
            {
                webSubSkippedCount++;
                _logger.LogDebug("Skipping channel {ChannelId} - WebSub working reliably (last notification: {LastNotification})", 
                    channel.ChannelId, channel.LastWebSubNotification);
                continue;
            }

            // Determine if this channel needs reconciliation based on its activity
            var nextReconciliationTime = CalculateNextReconciliationTime(channel);

            if (now >= nextReconciliationTime)
            {
                try
                {
                    var newVideos = await reconciliationService.ReconcileSingleChannelAsync(
                        channel, 
                        dbContext, 
                        cancellationToken);

                    reconciledCount++;
                    
                    if (newVideos > 0)
                    {
                        _logger.LogInformation("Reconciled channel {ChannelId}: found {Count} new videos", 
                            channel.ChannelId, newVideos);
                    }
                    else
                    {
                        _logger.LogDebug("Reconciled channel {ChannelId}: no new videos", channel.ChannelId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reconciling channel {ChannelId}", channel.ChannelId);
                }
            }
            else
            {
                skippedCount++;
                var timeUntilNext = nextReconciliationTime - now;
                _logger.LogDebug("Skipping channel {ChannelId}, next reconciliation in {TimeUntilNext}", 
                    channel.ChannelId, timeUntilNext);
            }
        }

        _logger.LogInformation("Reconciliation cycle complete: {Reconciled} channels reconciled, {Skipped} skipped by schedule, {WebSubSkipped} skipped due to WebSub", 
            reconciledCount, skippedCount, webSubSkippedCount);
    }
    
    /// <summary>
    /// Determines if a channel should skip reconciliation because WebSub is working reliably.
    /// </summary>
    private bool ShouldSkipDueToWebSub(Channel channel)
    {
        var options = _options.Value;
        
        // If we haven't received a WebSub notification, don't skip
        if (channel.LastWebSubNotification == null)
        {
            return false;
        }
        
        // If WebSub notification was recent, we can trust it's working
        var timeSinceLastWebSub = DateTimeOffset.UtcNow - channel.LastWebSubNotification.Value;
        var threshold = TimeSpan.FromHours(options.WebSubReliabilityThresholdHours);
        
        // For inactive channels: if they haven't had WebSub activity in a long time,
        // we should occasionally check if they became active (but still very infrequently)
        // This handles the case where WebSub might have stopped working for a dormant channel
        if (channel.AveragePublishInterval.HasValue)
        {
            var avgInterval = channel.AveragePublishInterval.Value;
            
            // If channel is very inactive (>30 days between videos) and WebSub is stale,
            // don't skip - reconcile very infrequently to detect revival
            if (avgInterval > TimeSpan.FromDays(options.LowActivityThresholdDays))
            {
                var staleThreshold = TimeSpan.FromDays(options.WebSubStaleThresholdDays);
                if (timeSinceLastWebSub > staleThreshold)
                {
                    // WebSub seems stale for this inactive channel, don't skip
                    return false;
                }
            }
        }
        
        return timeSinceLastWebSub < threshold;
    }

    /// <summary>
    /// Calculates when a channel should next be reconciled based on its activity level.
    /// </summary>
    private DateTimeOffset CalculateNextReconciliationTime(Channel channel)
    {
        var options = _options.Value;
        var lastReconciled = channel.LastReconciledAt ?? DateTimeOffset.MinValue;

        // If never reconciled, reconcile now
        if (channel.LastReconciledAt == null)
        {
            return DateTimeOffset.MinValue;
        }

        // Calculate interval based on channel activity
        var interval = CalculateReconciliationInterval(channel);

        return lastReconciled.Add(interval);
    }

    /// <summary>
    /// Calculates the appropriate reconciliation interval for a channel based on its publishing frequency.
    /// </summary>
    private TimeSpan CalculateReconciliationInterval(Channel channel)
    {
        var options = _options.Value;
        var baseInterval = TimeSpan.FromMinutes(options.ReconciliationBaseIntervalMinutes);

        // If we don't have activity data yet, use base interval
        if (channel.AveragePublishInterval == null)
        {
            return baseInterval;
        }

        var avgInterval = channel.AveragePublishInterval.Value;
        var multiplier = 1.0;

        // Determine activity level and apply corresponding multiplier using configurable thresholds
        if (avgInterval <= TimeSpan.FromDays(options.HighActivityThresholdDays))
        {
            // High activity: publishes daily or more
            multiplier = options.ReconciliationHighActivityMultiplier;
        }
        else if (avgInterval <= TimeSpan.FromDays(options.MediumActivityThresholdDays))
        {
            // Medium activity: publishes weekly
            multiplier = options.ReconciliationMediumActivityMultiplier;
        }
        else if (avgInterval <= TimeSpan.FromDays(options.LowActivityThresholdDays))
        {
            // Low activity: publishes monthly
            multiplier = options.ReconciliationLowActivityMultiplier;
        }
        else
        {
            // Inactive: publishes less than monthly
            multiplier = options.ReconciliationInactiveMultiplier;
        }

        var calculatedInterval = baseInterval * multiplier;

        // Apply min/max bounds
        var minInterval = TimeSpan.FromHours(options.ReconciliationMinIntervalHours);
        var maxInterval = TimeSpan.FromDays(options.ReconciliationMaxIntervalDays);

        if (calculatedInterval < minInterval)
        {
            return minInterval;
        }
        else if (calculatedInterval > maxInterval)
        {
            return maxInterval;
        }

        return calculatedInterval;
    }
}
