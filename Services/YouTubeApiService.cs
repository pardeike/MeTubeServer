using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MeTubeServer.Models;

namespace MeTubeServer.Services;

public class YouTubeApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<YouTubeApiService> _logger;
    private readonly string _apiKey;
    private readonly YouTubeQuotaTracker _quotaTracker;
    private readonly YouTubeApiStatusService _apiStatus;

    public YouTubeApiService(
        HttpClient httpClient, 
        IOptions<YouTubeOptions> options, 
        ILogger<YouTubeApiService> logger,
        YouTubeQuotaTracker quotaTracker,
        YouTubeApiStatusService apiStatus)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = options.Value.ApiKey;
        _quotaTracker = quotaTracker;
        _apiStatus = apiStatus;
        _httpClient.BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/");
    }

    /// <summary>
    /// Checks if the API response indicates a quota exceeded error.
    /// </summary>
    private static bool IsQuotaExceededResponse(HttpResponseMessage response, string? body = null)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden)
            return false;

        // If we have the body, check for quota-specific error (case-insensitive)
        if (!string.IsNullOrEmpty(body))
        {
            return body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase) || 
                   body.Contains("youtube.quota", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Handles a quota exceeded response by recording the status.
    /// </summary>
    private void HandleQuotaExceeded()
    {
        _apiStatus.RecordQuotaExceeded();
    }

    /// <summary>
    /// Checks if the YouTube API is currently available. If not, logs a warning.
    /// </summary>
    /// <returns>True if API is available, false if quota is exceeded.</returns>
    private bool CheckApiAvailability(string operation)
    {
        if (!_apiStatus.IsApiAvailable)
        {
            _logger.LogDebug("Skipping YouTube API call ({Operation}) - quota exceeded, waiting for reset", operation);
            return false;
        }
        return true;
    }

    public async Task<string?> GetUploadsPlaylistIdAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (!CheckApiAvailability("GetUploadsPlaylistIdAsync"))
            return null;

        try
        {
            var response = await _httpClient.GetAsync(
                $"channels?part=contentDetails&id={channelId}&key={_apiKey}",
                cancellationToken);

            // Handle 404 gracefully - channel may be deleted or terminated
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Channel {ChannelId} not found (404) - may be deleted or terminated", channelId);
                return null;
            }

            // Handle quota exceeded
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (IsQuotaExceededResponse(response, body))
                {
                    HandleQuotaExceeded();
                    return null;
                }
            }

            response.EnsureSuccessStatusCode();
            _quotaTracker.RecordQuotaUsage("channels.list", 1); // channels.list with contentDetails = 1 unit
            _apiStatus.RecordApiAvailable(); // API is working
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<ChannelsResponse>(json);

            return result?.Items?.FirstOrDefault()?.ContentDetails?.RelatedPlaylists?.Uploads;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching uploads playlist ID for channel {ChannelId}", channelId);
            return null;
        }
    }

    /// <summary>
    /// Validates that the YouTube API key is working by making a simple API call.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="skipStatusCheck">If true, skips the API availability check. Used for recovery polling.</param>
    /// <returns>True if API key is valid and working, false if quota exceeded or invalid key.</returns>
    public async Task<bool> ValidateApiKeyAsync(CancellationToken cancellationToken = default, bool skipStatusCheck = false)
    {
        if (!skipStatusCheck && !CheckApiAvailability("ValidateApiKeyAsync"))
            return false;

        try
        {
            // Make a minimal API call to validate the key
            // Using the search endpoint with a simple query
            var response = await _httpClient.GetAsync(
                $"search?part=snippet&maxResults=1&type=video&q=test&key={_apiKey}",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _quotaTracker.RecordQuotaUsage("search.list", 100); // search.list = 100 units
                _logger.LogInformation("YouTube API key validated successfully");
                _apiStatus.RecordApiAvailable(); // API is working
                return true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                
                // Check for quota exceeded error
                if (IsQuotaExceededResponse(response, body))
                {
                    _logger.LogWarning("YouTube API key validation failed due to quota exceeded. Status: {StatusCode}", 
                        response.StatusCode);
                    HandleQuotaExceeded();
                    return false;
                }
                
                _logger.LogError("YouTube API key validation failed. Status: {StatusCode}, Body: {Body}",
                    response.StatusCode, body);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating YouTube API key");
            return false;
        }
    }

    /// <summary>
    /// Gets uploads playlist IDs for multiple channels in batched API calls (up to 50 channels per request).
    /// </summary>
    /// <param name="channelIds">List of channel IDs to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping channel IDs to uploads playlist IDs.</returns>
    public async Task<Dictionary<string, string>> GetUploadsPlaylistIdsBatchAsync(
        List<string> channelIds,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, string>();
        
        if (channelIds == null || channelIds.Count == 0)
            return results;

        if (!CheckApiAvailability("GetUploadsPlaylistIdsBatchAsync"))
            return results;

        try
        {
            // YouTube API allows up to 50 channel IDs per request
            var batches = channelIds.Chunk(50);

            foreach (var batch in batches)
            {
                var ids = string.Join(",", batch);
                var response = await _httpClient.GetAsync(
                    $"channels?part=contentDetails,snippet&id={ids}&key={_apiKey}",
                    cancellationToken);

                // Handle quota exceeded
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (IsQuotaExceededResponse(response, body))
                    {
                        HandleQuotaExceeded();
                        return results; // Return what we have so far
                    }
                }

                response.EnsureSuccessStatusCode();
                _quotaTracker.RecordQuotaUsage("channels.list", 1); // channels.list = 1 unit
                _apiStatus.RecordApiAvailable(); // API is working
                
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<ChannelsResponse>(json);

                if (result?.Items != null)
                {
                    foreach (var item in result.Items)
                    {
                        if (!string.IsNullOrEmpty(item.Id) && 
                            !string.IsNullOrEmpty(item.ContentDetails?.RelatedPlaylists?.Uploads))
                        {
                            results[item.Id] = item.ContentDetails.RelatedPlaylists.Uploads;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching uploads playlist IDs in batch");
        }

        return results;
    }

    /// <summary>
    /// Gets channel metadata including name and thumbnail.
    /// </summary>
    public async Task<ChannelMetadata?> GetChannelMetadataAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        if (!CheckApiAvailability("GetChannelMetadataAsync"))
            return null;

        try
        {
            var response = await _httpClient.GetAsync(
                $"channels?part=snippet&id={channelId}&key={_apiKey}",
                cancellationToken);

            // Handle 404 gracefully - channel may be deleted or terminated
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Channel {ChannelId} not found (404) - may be deleted or terminated", channelId);
                return null;
            }

            // Handle quota exceeded
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (IsQuotaExceededResponse(response, body))
                {
                    HandleQuotaExceeded();
                    return null;
                }
            }

            response.EnsureSuccessStatusCode();
            _quotaTracker.RecordQuotaUsage("channels.list", 1); // channels.list = 1 unit
            _apiStatus.RecordApiAvailable(); // API is working
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<ChannelsResponse>(json);

            var item = result?.Items?.FirstOrDefault();
            if (item?.Snippet != null)
            {
                return new ChannelMetadata
                {
                    ChannelId = channelId,
                    Title = item.Snippet.Title,
                    ThumbnailUrl = item.Snippet.Thumbnails?.Medium?.Url
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching channel metadata for {ChannelId}", channelId);
            return null;
        }
    }

    public class ChannelMetadata
    {
        public string ChannelId { get; set; } = null!;
        public string? Title { get; set; }
        public string? ThumbnailUrl { get; set; }
    }

    public async Task<List<PlaylistItem>> GetPlaylistItemsAsync(
        string playlistId,
        DateTimeOffset? publishedAfter = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        if (!CheckApiAvailability("GetPlaylistItemsAsync"))
            return new List<PlaylistItem>();

        try
        {
            var url = $"playlistItems?part=snippet,contentDetails&playlistId={playlistId}&maxResults={maxResults}&key={_apiKey}";
            
            var response = await _httpClient.GetAsync(url, cancellationToken);
            
            // Handle 404 gracefully - playlist may be deleted or private
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Playlist {PlaylistId} not found (404) - may be deleted or private", playlistId);
                return new List<PlaylistItem>();
            }
            
            // Handle quota exceeded
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (IsQuotaExceededResponse(response, body))
                {
                    HandleQuotaExceeded();
                    return new List<PlaylistItem>();
                }
            }
            
            response.EnsureSuccessStatusCode();
            _quotaTracker.RecordQuotaUsage("playlistItems.list", 1); // playlistItems.list = 1 unit
            _apiStatus.RecordApiAvailable(); // API is working
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<PlaylistItemsResponse>(json);

            var items = result?.Items ?? new List<PlaylistItem>();
            
            if (publishedAfter.HasValue)
            {
                items = items.Where(i => i.Snippet?.PublishedAt > publishedAfter.Value).ToList();
            }

            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching playlist items for playlist {PlaylistId}", playlistId);
            return new List<PlaylistItem>();
        }
    }

    public async Task<List<VideoDetails>> GetVideosDetailsAsync(
        List<string> videoIds,
        CancellationToken cancellationToken = default)
    {
        if (videoIds == null || videoIds.Count == 0)
            return new List<VideoDetails>();

        if (!CheckApiAvailability("GetVideosDetailsAsync"))
            return new List<VideoDetails>();

        try
        {
            // YouTube API allows up to 50 video IDs per request
            var batches = videoIds.Chunk(50);
            var allVideos = new List<VideoDetails>();

            foreach (var batch in batches)
            {
                var ids = string.Join(",", batch);
                var url = $"videos?part=snippet,contentDetails&id={ids}&key={_apiKey}";
                
                var response = await _httpClient.GetAsync(url, cancellationToken);
                
                // Handle quota exceeded
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (IsQuotaExceededResponse(response, body))
                    {
                        HandleQuotaExceeded();
                        return allVideos; // Return what we have so far
                    }
                }
                
                response.EnsureSuccessStatusCode();
                _quotaTracker.RecordQuotaUsage("videos.list", 1); // videos.list = 1 unit
                _apiStatus.RecordApiAvailable(); // API is working
                
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<VideosResponse>(json);
                
                if (result?.Items != null)
                    allVideos.AddRange(result.Items);
            }

            return allVideos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching video details");
            return new List<VideoDetails>();
        }
    }

    // Response models for YouTube Data API
    public class ChannelsResponse
    {
        [JsonPropertyName("items")]
        public List<ChannelItem>? Items { get; set; }
    }

    public class ChannelItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("contentDetails")]
        public ContentDetails? ContentDetails { get; set; }

        [JsonPropertyName("snippet")]
        public ChannelSnippet? Snippet { get; set; }
    }

    public class ChannelSnippet
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("thumbnails")]
        public Thumbnails? Thumbnails { get; set; }
    }

    public class ContentDetails
    {
        [JsonPropertyName("relatedPlaylists")]
        public RelatedPlaylists? RelatedPlaylists { get; set; }
    }

    public class RelatedPlaylists
    {
        [JsonPropertyName("uploads")]
        public string? Uploads { get; set; }
    }

    public class PlaylistItemsResponse
    {
        [JsonPropertyName("items")]
        public List<PlaylistItem>? Items { get; set; }
    }

    public class PlaylistItem
    {
        [JsonPropertyName("snippet")]
        public PlaylistItemSnippet? Snippet { get; set; }

        [JsonPropertyName("contentDetails")]
        public PlaylistItemContentDetails? ContentDetails { get; set; }
    }

    public class PlaylistItemSnippet
    {
        [JsonPropertyName("publishedAt")]
        public DateTimeOffset PublishedAt { get; set; }

        [JsonPropertyName("channelId")]
        public string? ChannelId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("thumbnails")]
        public Thumbnails? Thumbnails { get; set; }

        [JsonPropertyName("resourceId")]
        public ResourceId? ResourceId { get; set; }
    }

    public class PlaylistItemContentDetails
    {
        [JsonPropertyName("videoId")]
        public string? VideoId { get; set; }
    }

    public class ResourceId
    {
        [JsonPropertyName("videoId")]
        public string? VideoId { get; set; }
    }

    public class Thumbnails
    {
        [JsonPropertyName("medium")]
        public Thumbnail? Medium { get; set; }

        [JsonPropertyName("high")]
        public Thumbnail? High { get; set; }
    }

    public class Thumbnail
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    public class VideosResponse
    {
        [JsonPropertyName("items")]
        public List<VideoDetails>? Items { get; set; }
    }

    public class VideoDetails
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("snippet")]
        public VideoSnippet? Snippet { get; set; }

        [JsonPropertyName("contentDetails")]
        public VideoContentDetails? ContentDetails { get; set; }
    }

    public class VideoSnippet
    {
        [JsonPropertyName("publishedAt")]
        public DateTimeOffset PublishedAt { get; set; }

        [JsonPropertyName("channelId")]
        public string? ChannelId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("thumbnails")]
        public Thumbnails? Thumbnails { get; set; }
    }

    public class VideoContentDetails
    {
        [JsonPropertyName("duration")]
        public string? Duration { get; set; }
    }
}
