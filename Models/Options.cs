namespace MeTubeServer.Models;

public class YouTubeOptions
{
    public string ApiKey { get; set; } = null!;
    public string HubUrl { get; set; } = "https://pubsubhubbub.appspot.com/subscribe";
    public string CallbackBaseUrl { get; set; } = null!;
}

public class HubOptions
{
    public int WebSubSharedSecretLength { get; set; } = 32;
}
