using System.ComponentModel.DataAnnotations;

namespace MeTubeServer.Models;

/// <summary>
/// Request for registering channel subscriptions for a user.
/// </summary>
public class RegisterChannelsRequest
{
    /// <summary>
    /// List of YouTube channel IDs to register.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one channel ID is required")]
    [MaxLength(100, ErrorMessage = "Maximum 100 channels can be registered at once")]
    public List<string> ChannelIds { get; set; } = new();
}

/// <summary>
/// Response containing a feed of videos.
/// </summary>
public class FeedResponse
{
    /// <summary>
    /// List of videos in the feed.
    /// </summary>
    public IReadOnlyList<VideoDto> Videos { get; set; } = Array.Empty<VideoDto>();
    
    /// <summary>
    /// Cursor for fetching the next page of results. Use this as the 'since' parameter.
    /// </summary>
    public string? NextCursor { get; set; }
    
    /// <summary>
    /// Deprecated: Use NextCursor instead.
    /// </summary>
    [Obsolete("Use NextCursor instead")]
    public string? NextPageToken { get; set; }
}

/// <summary>
/// Video metadata.
/// </summary>
public class VideoDto
{
    /// <summary>
    /// YouTube video ID.
    /// </summary>
    public string VideoId { get; set; } = null!;
    
    /// <summary>
    /// YouTube channel ID that published the video.
    /// </summary>
    public string ChannelId { get; set; } = null!;
    
    /// <summary>
    /// When the video was published.
    /// </summary>
    public DateTimeOffset PublishedAt { get; set; }
    
    /// <summary>
    /// Video title.
    /// </summary>
    public string? Title { get; set; }
    
    /// <summary>
    /// Video description.
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// URL to video thumbnail image.
    /// </summary>
    public string? ThumbnailUrl { get; set; }
    
    /// <summary>
    /// Video duration.
    /// </summary>
    public TimeSpan? Duration { get; set; }
}
