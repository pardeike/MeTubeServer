using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;
using MeTubeServer.Models;

namespace MeTubeServer.Services;

/// <summary>
/// Service for reconciling channel videos by fetching recent uploads from YouTube API.
/// This is a backup mechanism for videos that may have been missed by WebSub notifications.
/// </summary>
public class ReconciliationService
{
    private readonly YouTubeApiService _youtubeApi;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        YouTubeApiService youtubeApi,
        IBackgroundTaskQueue taskQueue,
        ILogger<ReconciliationService> logger)
    {
        _youtubeApi = youtubeApi;
        _taskQueue = taskQueue;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles videos for a specific user's channels.
    /// </summary>
    /// <param name="userId">User ID to reconcile channels for</param>
    /// <param name="db">Database context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of new videos discovered</returns>
    public async Task<int> ReconcileUserChannelsAsync(
        int userId,
        MeTubeDbContext db,
        CancellationToken cancellationToken = default)
    {
        // Get all channels for this user
        var channels = await db.UserChannels
            .Where(uc => uc.UserId == userId)
            .Include(uc => uc.Channel)
            .Select(uc => uc.Channel)
            .Where(c => c.UploadsPlaylistId != null)
            .ToListAsync(cancellationToken);

        if (channels.Count == 0)
        {
            _logger.LogDebug("No channels to reconcile for user {UserId}", userId);
            return 0;
        }

        _logger.LogInformation("Reconciling {Count} channels for user {UserId}", channels.Count, userId);

        int totalNewVideos = 0;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                var newVideos = await ReconcileChannelAsync(channel, db, cancellationToken);
                totalNewVideos += newVideos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling channel {ChannelId} for user {UserId}", 
                    channel.ChannelId, userId);
            }
        }

        _logger.LogInformation("Reconciled {TotalNewVideos} new videos for user {UserId}", 
            totalNewVideos, userId);
        
        return totalNewVideos;
    }

    /// <summary>
    /// Reconciles videos for a single channel.
    /// </summary>
    private async Task<int> ReconcileChannelAsync(
        Channel channel,
        MeTubeDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(channel.UploadsPlaylistId))
            return 0;

        // Add 1 hour overlap to account for clock skew and missing videos
        var since = channel.LastSeenPublishedAt?.AddHours(-1);

        var items = await _youtubeApi.GetPlaylistItemsAsync(
            channel.UploadsPlaylistId,
            since,
            20,
            cancellationToken);

        if (items.Count == 0)
            return 0;

        _logger.LogDebug("Found {Count} items for channel {ChannelId}", items.Count, channel.ChannelId);

        int newVideosCount = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var videoId = item.ContentDetails?.VideoId ?? item.Snippet?.ResourceId?.VideoId;
            if (string.IsNullOrEmpty(videoId))
                continue;

            var publishedAt = item.Snippet?.PublishedAt ?? DateTimeOffset.UtcNow;

            // Check if we already have this video
            var existingVideo = await db.Videos
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

                db.Videos.Add(video);
                newVideosCount++;

                // Update channel's last seen timestamp
                if (publishedAt > (channel.LastSeenPublishedAt ?? DateTimeOffset.MinValue))
                {
                    channel.LastSeenPublishedAt = publishedAt;
                }

                _logger.LogInformation("Added video {VideoId} for channel {ChannelId}", videoId, channel.ChannelId);
                
                // Queue video enrichment to fetch duration and other metadata
                await _taskQueue.QueueBackgroundWorkItemAsync(async (sp, ct) =>
                {
                    var enrichmentService = sp.GetRequiredService<VideoEnrichmentService>();
                    var dbContextForEnrichment = sp.GetRequiredService<MeTubeDbContext>();
                    await enrichmentService.EnrichVideoAsync(videoId, dbContextForEnrichment, ct);
                });
            }
        }

        if (newVideosCount > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return newVideosCount;
    }
}
