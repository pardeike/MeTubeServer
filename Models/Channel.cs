namespace MeTubeServer.Models;

public class Channel
{
    public int Id { get; set; }
    public string ChannelId { get; set; } = null!;
    public string? UploadsPlaylistId { get; set; }
    public string TopicUrl { get; set; } = null!;
    public string? HubSecret { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LastSeenPublishedAt { get; set; }
    public DateTimeOffset? LastWebSubNotification { get; set; }
    
    // Channel metadata
    public string? ChannelName { get; set; }
    public string? ChannelThumbnailUrl { get; set; }
    public DateTimeOffset? MetadataLastUpdated { get; set; }
    
    public ICollection<UserChannel> UserChannels { get; set; } = new List<UserChannel>();
    public ICollection<Video> Videos { get; set; } = new List<Video>();
}
