using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;
using MeTubeServer.Services;

namespace MeTubeServer.BackgroundJobs;

/// <summary>
/// Background job that periodically cleans up orphaned channels that no users follow.
/// </summary>
public class ChannelCleanupJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChannelCleanupJob> _logger;
    private readonly BackgroundJobHealthCheck _healthCheck;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);

    public ChannelCleanupJob(
        IServiceProvider serviceProvider,
        ILogger<ChannelCleanupJob> logger,
        BackgroundJobHealthCheck healthCheck)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _healthCheck = healthCheck;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Channel Cleanup Job started");

        // Wait a bit before first run to let the system stabilize
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOrphanedChannelsAsync(stoppingToken);
                _healthCheck.RecordJobExecution(nameof(ChannelCleanupJob), _interval);
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in channel cleanup job");
                _healthCheck.RecordJobError(nameof(ChannelCleanupJob));
            }
        }

        _logger.LogInformation("Channel Cleanup Job stopped");
    }

    private async Task CleanupOrphanedChannelsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
        var webSubService = scope.ServiceProvider.GetRequiredService<WebSubService>();

        // Find channels with no user subscriptions
        var orphanedChannels = await dbContext.Channels
            .Include(c => c.UserChannels)
            .Where(c => !c.UserChannels.Any())
            .ToListAsync(cancellationToken);

        if (orphanedChannels.Count == 0)
        {
            _logger.LogDebug("No orphaned channels found");
            return;
        }

        _logger.LogInformation("Found {Count} orphaned channels to clean up", orphanedChannels.Count);

        foreach (var channel in orphanedChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Unsubscribe from WebSub
                await webSubService.UnsubscribeAsync(channel.TopicUrl, cancellationToken);
                
                // Remove the channel from database
                dbContext.Channels.Remove(channel);
                
                _logger.LogInformation("Cleaned up orphaned channel {ChannelId}", channel.ChannelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up channel {ChannelId}", channel.ChannelId);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
