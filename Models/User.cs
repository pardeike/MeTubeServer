namespace MeTubeServer.Models;

public class User
{
    public int Id { get; set; }
    public string AppUserId { get; set; } = null!;
    public ICollection<UserChannel> UserChannels { get; set; } = new List<UserChannel>();
}
