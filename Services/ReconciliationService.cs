using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;
using MeTubeServer.Models;
using Microsoft.Extensions.Options;

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
    private readonly TimeSpan _maxDetectionLag;
    private readonly ConcurrentDictionary<int, ChannelReconcileState> _channelReconcileStates = new();

    public ReconciliationService(
        YouTubeApiService youtubeApi,
        IBackgroundTaskQueue taskQueue,
        ILogger<ReconciliationService> logger,
        IOptions<ReconciliationSettings>? settings = null)
    {
        _youtubeApi = youtubeApi;
        _taskQueue = taskQueue;
        _logger = logger;
        var reconciliationSettings = settings?.Value ?? new ReconciliationSettings();
        _maxDetectionLag = reconciliationSettings.MaxDetectionLag;
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

            var state = _channelReconcileStates.TryGetValue(channel.Id, out var existingState)
                ? existingState
                : null;
            var cadence = await CalculateChannelCadenceAsync(channel, db, now, state, cancellationToken);
            state = GetOrCreateState(channel, cadence, state);
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
                UpdateReconcileState(channel, state, now, cadence, newVideos > 0);
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

    private async Task<ChannelCadence> CalculateChannelCadenceAsync(
        Channel channel,
        MeTubeDbContext db,
        DateTimeOffset now,
        ChannelReconcileState? state,
        CancellationToken cancellationToken)
    {
        var medianGap = await CalculateMedianGapAsync(channel, db, cancellationToken);
        var cadenceSeed = medianGap
            ?? (channel.LastSeenPublishedAt.HasValue ? now - channel.LastSeenPublishedAt.Value : TimeSpan.FromDays(1));

        var previousEma = state?.CadenceEma ?? cadenceSeed;
        var emaTicks = state == null
            ? cadenceSeed.Ticks
            : (long)(previousEma.Ticks * 0.7 + cadenceSeed.Ticks * 0.3);
        var ema = TimeSpan.FromTicks(emaTicks);

        var cadenceMultiplier = 1.0; // Allows tuning if future configs need more slack
        var baseInterval = TimeSpan.FromTicks((long)(ema.Ticks * cadenceMultiplier));
        baseInterval = Clamp(baseInterval, TimeSpan.FromHours(12), TimeSpan.FromDays(14));

        return new ChannelCadence(baseInterval, ema, medianGap);
    }

    private static async Task<TimeSpan?> CalculateMedianGapAsync(
        Channel channel,
        MeTubeDbContext db,
        CancellationToken cancellationToken)
    {
        var publishTimes = await db.Videos
            .Where(v => v.ChannelId == channel.Id)
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => v.PublishedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (publishTimes.Count < 2)
        {
            return null;
        }

        var gaps = new List<TimeSpan>();
        for (var i = 0; i < publishTimes.Count - 1; i++)
        {
            gaps.Add(publishTimes[i] - publishTimes[i + 1]);
        }

        gaps.Sort();
        var medianIndex = gaps.Count / 2;
        return gaps.Count % 2 == 0
            ? TimeSpan.FromTicks((gaps[medianIndex - 1].Ticks + gaps[medianIndex].Ticks) / 2)
            : gaps[medianIndex];
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private ChannelReconcileState GetOrCreateState(
        Channel channel,
        ChannelCadence cadence,
        ChannelReconcileState? existingState)
    {
        var state = _channelReconcileStates.AddOrUpdate(
            channel.Id,
            _ => new ChannelReconcileState(DateTimeOffset.MinValue, cadence.BaseInterval, 0, cadence.CadenceEma),
            (_, existing) => existing with
            {
                Interval = existing.Interval > cadence.BaseInterval ? existing.Interval : cadence.BaseInterval,
                CadenceEma = cadence.CadenceEma
            });

        return state;
    }

    private bool ShouldReconcileChannel(
        ChannelReconcileState state,
        DateTimeOffset now,
        out TimeSpan waitTime)
    {
        if (state.LastCheckedAt == DateTimeOffset.MinValue)
        {
            waitTime = TimeSpan.Zero;
            return true;
        }

        var nextCheck = state.LastCheckedAt + state.Interval;
        var latestAllowed = state.LastCheckedAt + _maxDetectionLag;
        if (nextCheck > latestAllowed)
        {
            nextCheck = latestAllowed;
        }

        if (now < nextCheck)
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
        ChannelCadence cadence,
        bool foundNewVideos)
    {
        var consecutiveMisses = foundNewVideos
            ? ReduceEmptyStreak(state.ConsecutiveNoNewVideos, state.Interval)
            : state.ConsecutiveNoNewVideos + 1;
        var adaptiveInterval = CalculateAdaptiveInterval(cadence.BaseInterval, consecutiveMisses);

        _channelReconcileStates[channel.Id] = state with
        {
            LastCheckedAt = checkedAt,
            Interval = adaptiveInterval,
            ConsecutiveNoNewVideos = consecutiveMisses,
            CadenceEma = cadence.CadenceEma
        };
    }

    private static TimeSpan CalculateAdaptiveInterval(TimeSpan baseInterval, int consecutiveMisses)
    {
        var multiplier = 1 + Math.Min(consecutiveMisses, 6); // cap multiplier growth
        var scaled = TimeSpan.FromTicks(baseInterval.Ticks * multiplier);
        var maxInterval = TimeSpan.FromDays(30);
        return scaled > maxInterval ? maxInterval : scaled;
    }

    private static int ReduceEmptyStreak(int currentStreak, TimeSpan previousInterval)
    {
        if (currentStreak <= 0)
        {
            return 0;
        }

        return previousInterval >= TimeSpan.FromDays(7)
            ? Math.Max(currentStreak / 2, 0)
            : Math.Max(currentStreak - 1, 0);
    }

    private sealed record ChannelCadence(TimeSpan BaseInterval, TimeSpan CadenceEma, TimeSpan? MedianGap);

    private sealed record ChannelReconcileState(
        DateTimeOffset LastCheckedAt,
        TimeSpan Interval,
        int ConsecutiveNoNewVideos,
        TimeSpan CadenceEma);
}
