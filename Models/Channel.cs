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
    
    public ICollection<UserChannel> UserChannels { get; set; } = new List<UserChannel>();
    public ICollection<Video> Videos { get; set; } = new List<Video>();
}
