namespace MeTubeServer.Models;

/// <summary>
/// Configuration options for YouTube API integration.
/// </summary>
public class YouTubeOptions
{
    /// <summary>
    /// YouTube Data API key.
    /// </summary>
    public string ApiKey { get; set; } = null!;
    
    /// <summary>
    /// WebSub hub URL for YouTube notifications.
    /// </summary>
    public string HubUrl { get; set; } = "https://pubsubhubbub.appspot.com/subscribe";
    
    /// <summary>
    /// Public HTTPS URL where this server is accessible (for WebSub callbacks).
    /// </summary>
    public string CallbackBaseUrl { get; set; } = null!;
}

/// <summary>
/// Configuration options for the hub server.
/// </summary>
public class HubOptions
{
    /// <summary>
    /// Length of WebSub shared secret.
    /// </summary>
    public int WebSubSharedSecretLength { get; set; } = 32;
    
    /// <summary>
    /// Default limit for feed queries.
    /// </summary>
    public int DefaultFeedLimit { get; set; } = 50;
    
    /// <summary>
    /// Maximum limit for feed queries.
    /// </summary>
    public int MaxFeedLimit { get; set; } = 100;
    
    /// <summary>
    /// Maximum results per reconciliation job.
    /// </summary>
    public int ReconciliationMaxResults { get; set; } = 20;
    
    /// <summary>
    /// Maximum request body size in bytes (1 MB default).
    /// </summary>
    public long MaxRequestBodySize { get; set; } = 1024 * 1024;
    
    /// <summary>
    /// HTTP client timeout in seconds.
    /// </summary>
    public int HttpClientTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Shutdown timeout in seconds.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Background task queue capacity.
    /// </summary>
    public int BackgroundTaskQueueCapacity { get; set; } = 100;
}
