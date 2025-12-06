namespace MeTubeServer.Models;

public class UserChannel
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
    
    public DateTimeOffset? UserLastSeenPublishedAt { get; set; }
}
