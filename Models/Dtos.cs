namespace MeTubeServer.Models;

public class RegisterChannelsRequest
{
    public List<string> ChannelIds { get; set; } = new();
}

public class FeedResponse
{
    public List<VideoDto> Videos { get; set; } = new();
    public string? NextPageToken { get; set; }
}

public class VideoDto
{
    public string VideoId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public DateTimeOffset PublishedAt { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public TimeSpan? Duration { get; set; }
}
