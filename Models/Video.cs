namespace MeTubeServer.Models;

public class Video
{
    public int Id { get; set; }
    public string VideoId { get; set; } = null!;
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
    
    public DateTimeOffset PublishedAt { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public TimeSpan? Duration { get; set; }
}
