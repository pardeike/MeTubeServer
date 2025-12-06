using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;
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

        var channelsToRenew = await dbContext.Channels
            .Where(c => c.LeaseExpiresAt == null || c.LeaseExpiresAt < expiryThreshold)
            .ToListAsync(cancellationToken);

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
