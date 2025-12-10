using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<int, ChannelReconcileState> _channelReconcileStates = new();

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

        var now = DateTimeOffset.UtcNow;
        _logger.LogInformation("Reconciling {Count} channels for user {UserId}", channels.Count, userId);

        int totalNewVideos = 0;
        var skippedChannels = 0;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var baseInterval = CalculateReconcileInterval(channel, now);
            var state = GetOrCreateState(channel, baseInterval);
            if (!ShouldReconcileChannel(state, now, out var waitTime))
            {
                skippedChannels++;
                _logger.LogDebug(
                    "Skipping reconciliation for channel {ChannelId}; next eligible in {WaitTime:g} (interval {Interval:g})",
                    channel.ChannelId,
                    waitTime,
                    state.Interval);
                continue;
            }

            var newVideos = 0;
            try
            {
                newVideos = await ReconcileChannelAsync(channel, db, cancellationToken);
                totalNewVideos += newVideos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling channel {ChannelId} for user {UserId}",
                    channel.ChannelId, userId);
            }
            finally
            {
                UpdateReconcileState(channel, state, now, baseInterval, newVideos > 0);
            }
        }

        _logger.LogInformation(
            "Reconciled {TotalNewVideos} new videos for user {UserId}. Skipped {Skipped} channels due to cadence-aware throttling",
            totalNewVideos,
            userId,
            skippedChannels);

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

    private static TimeSpan CalculateReconcileInterval(Channel channel, DateTimeOffset now)
    {
        if (channel.LastSeenPublishedAt is null)
        {
            return TimeSpan.FromHours(6); // New or uninitialized channels get a quick first pass
        }

        var age = now - channel.LastSeenPublishedAt.Value;

        if (age <= TimeSpan.FromDays(7))
            return TimeSpan.FromHours(12); // Active weekly uploaders

        if (age <= TimeSpan.FromDays(30))
            return TimeSpan.FromDays(1); // Typical cadence: daily or weekly

        if (age <= TimeSpan.FromDays(90))
            return TimeSpan.FromDays(3); // Monthly-ish channels

        return TimeSpan.FromDays(14); // Long-tail channels; reconcile sparingly
    }

    private ChannelReconcileState GetOrCreateState(Channel channel, TimeSpan baseInterval)
    {
        var state = _channelReconcileStates.AddOrUpdate(
            channel.Id,
            _ => new ChannelReconcileState(DateTimeOffset.MinValue, baseInterval, 0),
            (_, existing) => existing with
            {
                Interval = existing.Interval > baseInterval ? existing.Interval : baseInterval
            });

        return state;
    }

    private static bool ShouldReconcileChannel(
        ChannelReconcileState state,
        DateTimeOffset now,
        out TimeSpan waitTime)
    {
        var nextCheck = state.LastCheckedAt == DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : state.LastCheckedAt + state.Interval;

        if (nextCheck != DateTimeOffset.MinValue && now < nextCheck)
        {
            waitTime = nextCheck - now;
            return false;
        }

        waitTime = TimeSpan.Zero;
        return true;
    }

    private void UpdateReconcileState(
        Channel channel,
        ChannelReconcileState state,
        DateTimeOffset checkedAt,
        TimeSpan baseInterval,
        bool foundNewVideos)
    {
        var consecutiveMisses = foundNewVideos ? 0 : state.ConsecutiveNoNewVideos + 1;
        var adaptiveInterval = foundNewVideos
            ? baseInterval
            : CalculateAdaptiveInterval(baseInterval, consecutiveMisses);

        _channelReconcileStates[channel.Id] = state with
        {
            LastCheckedAt = checkedAt,
            Interval = adaptiveInterval,
            ConsecutiveNoNewVideos = consecutiveMisses
        };
    }

    private static TimeSpan CalculateAdaptiveInterval(TimeSpan baseInterval, int consecutiveMisses)
    {
        var multiplier = 1 + Math.Min(consecutiveMisses, 6); // cap multiplier growth
        var scaled = TimeSpan.FromTicks(baseInterval.Ticks * multiplier);
        var maxInterval = TimeSpan.FromDays(30);
        return scaled > maxInterval ? maxInterval : scaled;
    }

    private sealed record ChannelReconcileState(DateTimeOffset LastCheckedAt, TimeSpan Interval, int ConsecutiveNoNewVideos);
}
