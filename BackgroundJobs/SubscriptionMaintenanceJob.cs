using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;
using MeTubeServer.Models;
using MeTubeServer.Services;

namespace MeTubeServer.BackgroundJobs;

public class SubscriptionMaintenanceJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionMaintenanceJob> _logger;
    private readonly BackgroundJobHealthCheck _healthCheck;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly TimeSpan _safetyMargin = TimeSpan.FromDays(1);

    public SubscriptionMaintenanceJob(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionMaintenanceJob> logger,
        BackgroundJobHealthCheck healthCheck)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _healthCheck = healthCheck;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Subscription Maintenance Job started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                await RenewSubscriptionsAsync(stoppingToken);
                _healthCheck.RecordJobExecution(nameof(SubscriptionMaintenanceJob), _interval);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in subscription maintenance job");
                _healthCheck.RecordJobError(nameof(SubscriptionMaintenanceJob));
            }
        }

        _logger.LogInformation("Subscription Maintenance Job stopped");
    }

    private async Task RenewSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
        var webSubService = scope.ServiceProvider.GetRequiredService<WebSubService>();

        var expiryThreshold = DateTimeOffset.UtcNow + _safetyMargin;

        // Fetch channels that need renewal: those without a lease or with an expiring lease
        // Split into two queries to work around EF Core SQLite translation limitations
        var channelsWithoutLease = await dbContext.Channels
            .Where(c => c.LeaseExpiresAt == null)
            .ToListAsync(cancellationToken);

        var channelsWithExpiringLease = await dbContext.Channels
            .Where(c => c.LeaseExpiresAt != null && c.LeaseExpiresAt < expiryThreshold)
            .ToListAsync(cancellationToken);

        // Combine results efficiently
        var channelsToRenew = new List<Channel>(channelsWithoutLease.Count + channelsWithExpiringLease.Count);
        channelsToRenew.AddRange(channelsWithoutLease);
        channelsToRenew.AddRange(channelsWithExpiringLease);

        _logger.LogInformation("Found {Count} channels to renew subscriptions", channelsToRenew.Count);

        foreach (var channel in channelsToRenew)
        {
            // Check for cancellation before processing each channel
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                var success = await webSubService.SubscribeAsync(
                    channel.TopicUrl,
                    channel.HubSecret ?? string.Empty,
                    cancellationToken);

                if (success)
                {
                    _logger.LogInformation("Renewed subscription for channel {ChannelId}", channel.ChannelId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renewing subscription for channel {ChannelId}", channel.ChannelId);
            }
        }
    }
}
