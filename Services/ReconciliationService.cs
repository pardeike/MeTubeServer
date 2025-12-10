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
    private readonly ReconciliationSettings _settings;
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
        _settings = settings?.Value ?? new ReconciliationSettings();
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

        var bucketCounts = Enum.GetValues<ActivityBucket>().ToDictionary(b => b, _ => 0);
        var skippedByWebSub = 0;
        var skippedByCadence = 0;
        var reconciledChannels = 0;
        var totalNewVideos = 0;

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = _channelReconcileStates.TryGetValue(channel.Id, out var existingState)
                ? existingState
                : null;

            var cadence = await CalculateChannelCadenceAsync(channel, db, now, state, cancellationToken);
            var bucket = DetermineActivityBucket(cadence.LastUploadAt, now);
            bucketCounts[bucket] += 1;

            var baseInterval = ComputeBaseInterval(cadence.CadenceEma, bucket);
            state = GetOrCreateState(channel, baseInterval, cadence, state);

            if (!ShouldReconcileChannel(channel, state, cadence, now, out var waitTime, out var reason))
            {
                if (reason == ReconcileDecisionReason.WebSubReliable)
                {
                    skippedByWebSub++;
                }
                else
                {
                    skippedByCadence++;
                }

                _logger.LogDebug(
                    "Skipping reconciliation for channel {ChannelId} ({ChannelName}); bucket {Bucket}, reason {Reason}, next eligible in {WaitTime:g} (interval {Interval:g})",
                    channel.ChannelId,
                    channel.ChannelName,
                    bucket,
                    reason,
                    waitTime,
                    state.Interval);
                continue;
            }

            var newVideos = 0;
            try
            {
                newVideos = await ReconcileChannelAsync(channel, db, cancellationToken);
                totalNewVideos += newVideos;
                reconciledChannels++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling channel {ChannelId} for user {UserId}", channel.ChannelId, userId);
            }
            finally
            {
                UpdateReconcileState(channel, state, now, cadence, bucket, baseInterval, newVideos > 0);
            }
        }

        _logger.LogInformation(
            "Reconciled {TotalNewVideos} new videos for user {UserId}. Reconciled {RanCount} channels. Skipped {CadenceSkips} cadence-gated and {WebSubSkips} WebSub-reliable channels. Buckets: hot={Hot}, warm={Warm}, cold={Cold}, frozen={Frozen}",
            totalNewVideos,
            userId,
            reconciledChannels,
            skippedByCadence,
            skippedByWebSub,
            bucketCounts[ActivityBucket.Hot],
            bucketCounts[ActivityBucket.Warm],
            bucketCounts[ActivityBucket.Cold],
            bucketCounts[ActivityBucket.Frozen]);

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

        var since = channel.LastSeenPublishedAt?.AddHours(-1);

        var items = await _youtubeApi.GetPlaylistItemsAsync(
            channel.UploadsPlaylistId,
            since,
            20,
            cancellationToken);

        if (items.Count == 0)
            return 0;

        _logger.LogDebug("Found {Count} items for channel {ChannelId}", items.Count, channel.ChannelId);

        var newVideosCount = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var videoId = item.ContentDetails?.VideoId ?? item.Snippet?.ResourceId?.VideoId;
            if (string.IsNullOrEmpty(videoId))
                continue;

            var publishedAt = item.Snippet?.PublishedAt ?? DateTimeOffset.UtcNow;

            var existingVideo = await db.Videos
                .FirstOrDefaultAsync(v => v.VideoId == videoId, cancellationToken);

            if (existingVideo == null)
            {
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

                if (publishedAt > (channel.LastSeenPublishedAt ?? DateTimeOffset.MinValue))
                {
                    channel.LastSeenPublishedAt = publishedAt;
                }

                _logger.LogInformation("Added video {VideoId} for channel {ChannelId}", videoId, channel.ChannelId);

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
        var publishTimes = await db.Videos
            .Where(v => v.ChannelId == channel.Id)
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => v.PublishedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        var medianGap = CalculateMedianGap(publishTimes);
        var lastUploadAt = publishTimes.FirstOrDefault();
        var cadenceSeed = medianGap
            ?? (lastUploadAt != default ? now - lastUploadAt : TimeSpan.FromDays(1));

        var previousEma = state?.CadenceEma ?? cadenceSeed;
        var emaTicks = state == null
            ? cadenceSeed.Ticks
            : (long)(previousEma.Ticks * 0.7 + cadenceSeed.Ticks * 0.3);
        var ema = TimeSpan.FromTicks(emaTicks);

        return new ChannelCadence(ema, medianGap, lastUploadAt == default ? null : lastUploadAt);
    }

    internal static TimeSpan? CalculateMedianGap(List<DateTimeOffset> publishTimes)
    {
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

    internal ChannelReconcileState GetOrCreateState(
        Channel channel,
        TimeSpan baseInterval,
        ChannelCadence cadence,
        ChannelReconcileState? existingState)
    {
        var state = _channelReconcileStates.AddOrUpdate(
            channel.Id,
            _ => new ChannelReconcileState(DateTimeOffset.MinValue, baseInterval, 0, cadence.CadenceEma),
            (_, existing) => existing with
            {
                Interval = existing.Interval > baseInterval ? existing.Interval : baseInterval,
                CadenceEma = cadence.CadenceEma
            });

        return state;
    }

    internal bool ShouldReconcileChannel(
        Channel channel,
        ChannelReconcileState state,
        ChannelCadence cadence,
        DateTimeOffset now,
        out TimeSpan waitTime,
        out ReconcileDecisionReason reason)
    {
        if (IsWebSubReliable(channel, cadence.LastUploadAt, now))
        {
            waitTime = TimeSpan.Zero;
            reason = ReconcileDecisionReason.WebSubReliable;
            return false;
        }

        if (state.LastCheckedAt == DateTimeOffset.MinValue)
        {
            waitTime = TimeSpan.Zero;
            reason = ReconcileDecisionReason.FirstRun;
            return true;
        }

        var nextCheck = state.LastCheckedAt + state.Interval;
        var latestAllowed = state.LastCheckedAt + _settings.MaxDetectionLag;
        if (nextCheck > latestAllowed)
        {
            nextCheck = latestAllowed;
        }

        if (now < nextCheck)
        {
            waitTime = nextCheck - now;
            reason = ReconcileDecisionReason.NotYetDue;
            return false;
        }

        waitTime = TimeSpan.Zero;
        reason = ReconcileDecisionReason.DueByCadence;
        return true;
    }

    private void UpdateReconcileState(
        Channel channel,
        ChannelReconcileState state,
        DateTimeOffset checkedAt,
        ChannelCadence cadence,
        ActivityBucket bucket,
        TimeSpan baseInterval,
        bool foundNewVideos)
    {
        var consecutiveMisses = foundNewVideos
            ? ReduceEmptyStreak(state.ConsecutiveNoNewVideos, state.Interval)
            : state.ConsecutiveNoNewVideos + 1;
        var adaptiveInterval = CalculateAdaptiveInterval(baseInterval, bucket, consecutiveMisses);

        _channelReconcileStates[channel.Id] = state with
        {
            LastCheckedAt = checkedAt,
            Interval = adaptiveInterval,
            ConsecutiveNoNewVideos = consecutiveMisses,
            CadenceEma = cadence.CadenceEma
        };
    }

    internal TimeSpan ComputeBaseInterval(TimeSpan cadenceEma, ActivityBucket bucket)
    {
        var adjustedCadence = TimeSpan.FromTicks((long)(cadenceEma.Ticks * _settings.CadenceMultiplier));
        var intervals = GetBucketIntervals(bucket);
        return Clamp(adjustedCadence, intervals.Min, intervals.Max);
    }

    internal TimeSpan CalculateAdaptiveInterval(TimeSpan baseInterval, ActivityBucket bucket, int consecutiveMisses)
    {
        var cappedMisses = Math.Min(consecutiveMisses, 4);
        var multiplier = 1 + cappedMisses * 0.5;
        var scaledTicks = (long)(baseInterval.Ticks * multiplier);
        var intervals = GetBucketIntervals(bucket);
        var scaled = TimeSpan.FromTicks(scaledTicks);
        var clamped = Clamp(scaled, baseInterval, intervals.BackoffMax);
        return clamped;
    }

    internal static int ReduceEmptyStreak(int currentStreak, TimeSpan previousInterval)
    {
        if (currentStreak <= 0)
        {
            return 0;
        }

        return previousInterval >= TimeSpan.FromDays(7)
            ? Math.Max(currentStreak / 2, 0)
            : Math.Max(currentStreak - 1, 0);
    }

    internal ActivityBucket DetermineActivityBucket(DateTimeOffset? lastUploadAt, DateTimeOffset now)
    {
        if (lastUploadAt == null)
        {
            return ActivityBucket.Frozen;
        }

        var age = now - lastUploadAt.Value;
        if (age <= _settings.HotThreshold)
        {
            return ActivityBucket.Hot;
        }

        if (age <= _settings.WarmThreshold)
        {
            return ActivityBucket.Warm;
        }

        if (age <= _settings.ColdThreshold)
        {
            return ActivityBucket.Cold;
        }

        return ActivityBucket.Frozen;
    }

    internal bool IsWebSubReliable(Channel channel, DateTimeOffset? lastUploadAt, DateTimeOffset now)
    {
        if (channel.LastWebSubNotification is null)
        {
            return false;
        }

        var lastNotification = channel.LastWebSubNotification.Value;
        if (now - lastNotification > _settings.WebSubReliabilityWindow)
        {
            return false;
        }

        if (lastUploadAt is { } uploadAt && now - uploadAt > _settings.WebSubColdChannelThreshold)
        {
            return false;
        }

        return true;
    }

    private ActivityBucketIntervals GetBucketIntervals(ActivityBucket bucket) => bucket switch
    {
        ActivityBucket.Hot => _settings.Hot,
        ActivityBucket.Warm => _settings.Warm,
        ActivityBucket.Cold => _settings.Cold,
        _ => _settings.Frozen
    };

    internal sealed record ChannelCadence(TimeSpan CadenceEma, TimeSpan? MedianGap, DateTimeOffset? LastUploadAt);

    internal sealed record ChannelReconcileState(
        DateTimeOffset LastCheckedAt,
        TimeSpan Interval,
        int ConsecutiveNoNewVideos,
        TimeSpan CadenceEma);
}

public enum ActivityBucket
{
    Hot,
    Warm,
    Cold,
    Frozen
}

public enum ReconcileDecisionReason
{
    FirstRun,
    NotYetDue,
    DueByCadence,
    WebSubReliable
}
