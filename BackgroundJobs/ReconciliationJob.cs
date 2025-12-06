using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;
using MeTubeServer.Models;
using MeTubeServer.Services;

namespace MeTubeServer.BackgroundJobs;

public class ReconciliationJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReconciliationJob> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(30);

    public ReconciliationJob(
        IServiceProvider serviceProvider,
        ILogger<ReconciliationJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reconciliation Job started");

        // Wait a bit before starting the first reconciliation
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileChannelsAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in reconciliation job");
            }
        }

        _logger.LogInformation("Reconciliation Job stopped");
    }

    private async Task ReconcileChannelsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
        var youtubeApi = scope.ServiceProvider.GetRequiredService<YouTubeApiService>();

        var channels = await dbContext.Channels
            .Where(c => c.UploadsPlaylistId != null)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Reconciling {Count} channels", channels.Count);

        foreach (var channel in channels)
        {
            try
            {
                await ReconcileChannelAsync(channel, dbContext, youtubeApi, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling channel {ChannelId}", channel.ChannelId);
            }
        }
    }

    private async Task ReconcileChannelAsync(
        Channel channel,
        MeTubeDbContext dbContext,
        YouTubeApiService youtubeApi,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(channel.UploadsPlaylistId))
            return;

        // Add a small overlap to account for clock skew
        var since = channel.LastSeenPublishedAt?.AddMinutes(-5);

        var items = await youtubeApi.GetPlaylistItemsAsync(
            channel.UploadsPlaylistId,
            since,
            20,
            cancellationToken);

        if (items.Count == 0)
            return;

        _logger.LogInformation("Found {Count} items for channel {ChannelId}", items.Count, channel.ChannelId);

        foreach (var item in items)
        {
            var videoId = item.ContentDetails?.VideoId ?? item.Snippet?.ResourceId?.VideoId;
            if (string.IsNullOrEmpty(videoId))
                continue;

            var publishedAt = item.Snippet?.PublishedAt ?? DateTimeOffset.UtcNow;

            // Check if we already have this video
            var existingVideo = await dbContext.Videos
                .FirstOrDefaultAsync(v => v.VideoId == videoId, cancellationToken);

            if (existingVideo == null)
            {
                // New video - add it
                var video = new Video
                {
                    VideoId = videoId,
                    ChannelId = channel.Id,
                    PublishedAt = publishedAt,
                    Title = item.Snippet?.Title,
                    Description = item.Snippet?.Description,
                    ThumbnailUrl = item.Snippet?.Thumbnails?.High?.Url ?? item.Snippet?.Thumbnails?.Medium?.Url
                };

                dbContext.Videos.Add(video);

                // Update channel's last seen timestamp
                if (publishedAt > (channel.LastSeenPublishedAt ?? DateTimeOffset.MinValue))
                {
                    channel.LastSeenPublishedAt = publishedAt;
                }

                _logger.LogInformation("Added video {VideoId} for channel {ChannelId}", videoId, channel.ChannelId);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
