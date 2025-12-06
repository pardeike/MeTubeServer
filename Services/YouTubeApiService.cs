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

    public YouTubeApiService(HttpClient httpClient, IOptions<YouTubeOptions> options, ILogger<YouTubeApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = options.Value.ApiKey;
        _httpClient.BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/");
    }

    public async Task<string?> GetUploadsPlaylistIdAsync(string channelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"channels?part=contentDetails&id={channelId}&key={_apiKey}",
                cancellationToken);

            response.EnsureSuccessStatusCode();
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

    public async Task<List<PlaylistItem>> GetPlaylistItemsAsync(
        string playlistId,
        DateTimeOffset? publishedAfter = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"playlistItems?part=snippet,contentDetails&playlistId={playlistId}&maxResults={maxResults}&key={_apiKey}";
            
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            
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
                response.EnsureSuccessStatusCode();
                
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
        [JsonPropertyName("contentDetails")]
        public ContentDetails? ContentDetails { get; set; }
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
