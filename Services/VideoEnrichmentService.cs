using Microsoft.EntityFrameworkCore;
using MeTubeServer.Data;

namespace MeTubeServer.Services;

/// <summary>
/// Service for enriching video metadata in the background.
/// </summary>
public class VideoEnrichmentService
{
    private readonly YouTubeApiService _youtubeApi;
    private readonly ILogger<VideoEnrichmentService> _logger;

    public VideoEnrichmentService(
        YouTubeApiService youtubeApi,
        ILogger<VideoEnrichmentService> logger)
    {
        _youtubeApi = youtubeApi;
        _logger = logger;
    }

    /// <summary>
    /// Enriches video metadata by fetching details from YouTube API.
    /// </summary>
    public async Task EnrichVideoAsync(string videoId, MeTubeDbContext db, CancellationToken cancellationToken = default)
    {
        try
        {
            var details = await _youtubeApi.GetVideosDetailsAsync(new List<string> { videoId }, cancellationToken);
            
            if (details.Count == 0)
            {
                _logger.LogWarning("No details found for video {VideoId}", videoId);
                return;
            }

            var videoDetails = details[0];
            var video = await db.Videos.FirstOrDefaultAsync(v => v.VideoId == videoId, cancellationToken);

            if (video == null)
            {
                _logger.LogWarning("Video {VideoId} not found in database", videoId);
                return;
            }

            // Update video metadata
            if (videoDetails.Snippet != null)
            {
                video.Description = videoDetails.Snippet.Description;
                video.ThumbnailUrl ??= videoDetails.Snippet.Thumbnails?.High?.Url 
                    ?? videoDetails.Snippet.Thumbnails?.Medium?.Url;
            }

            if (videoDetails.ContentDetails?.Duration != null)
            {
                var duration = Iso8601DurationParser.ParseDuration(videoDetails.ContentDetails.Duration);
                if (duration.HasValue)
                {
                    video.Duration = duration.Value;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Enriched metadata for video {VideoId}", videoId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching video metadata for {VideoId}", videoId);
        }
    }

    /// <summary>
    /// Enriches metadata for multiple videos in batch.
    /// </summary>
    public async Task EnrichVideosAsync(List<string> videoIds, MeTubeDbContext db, CancellationToken cancellationToken = default)
    {
        if (videoIds.Count == 0)
            return;

        try
        {
            // Fetch all videos in a single query to avoid N+1
            var videos = await db.Videos
                .Where(v => videoIds.Contains(v.VideoId))
                .ToListAsync(cancellationToken);
            
            var details = await _youtubeApi.GetVideosDetailsAsync(videoIds, cancellationToken);
            
            if (details.Count == 0)
            {
                _logger.LogWarning("No details found for {Count} videos", videoIds.Count);
                return;
            }

            foreach (var videoDetails in details)
            {
                if (string.IsNullOrEmpty(videoDetails.Id))
                    continue;

                var video = videos.FirstOrDefault(v => v.VideoId == videoDetails.Id);

                if (video == null)
                    continue;

                // Update video metadata
                if (videoDetails.Snippet != null)
                {
                    video.Description = videoDetails.Snippet.Description;
                    video.ThumbnailUrl ??= videoDetails.Snippet.Thumbnails?.High?.Url 
                        ?? videoDetails.Snippet.Thumbnails?.Medium?.Url;
                }

                if (videoDetails.ContentDetails?.Duration != null)
                {
                    var duration = Iso8601DurationParser.ParseDuration(videoDetails.ContentDetails.Duration);
                    if (duration.HasValue)
                    {
                        video.Duration = duration.Value;
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Enriched metadata for {Count} videos", details.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching video metadata for batch");
        }
    }
}
