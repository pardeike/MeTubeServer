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
    
    /// <summary>
    /// Comma-separated list of allowed CORS origins. Leave empty to disable CORS.
    /// Example: "https://example.com,https://app.example.com"
    /// </summary>
    public string CorsAllowedOrigins { get; set; } = string.Empty;
    
    /// <summary>
    /// Base interval for reconciliation job execution in minutes.
    /// </summary>
    public int ReconciliationBaseIntervalMinutes { get; set; } = 60;
    
    /// <summary>
    /// Multiplier for channels that publish daily or more frequently.
    /// </summary>
    public double ReconciliationHighActivityMultiplier { get; set; } = 2.0;
    
    /// <summary>
    /// Multiplier for channels that publish weekly.
    /// </summary>
    public double ReconciliationMediumActivityMultiplier { get; set; } = 12.0;
    
    /// <summary>
    /// Multiplier for channels that publish monthly.
    /// </summary>
    public double ReconciliationLowActivityMultiplier { get; set; } = 48.0;
    
    /// <summary>
    /// Multiplier for inactive channels (no videos in 30+ days).
    /// </summary>
    public double ReconciliationInactiveMultiplier { get; set; } = 168.0;
    
    /// <summary>
    /// Minimum reconciliation interval in hours.
    /// </summary>
    public double ReconciliationMinIntervalHours { get; set; } = 2.0;
    
    /// <summary>
    /// Maximum reconciliation interval in days.
    /// </summary>
    public double ReconciliationMaxIntervalDays { get; set; } = 7.0;
    
    /// <summary>
    /// Hours since last WebSub notification before considering WebSub unreliable.
    /// Channels with recent WebSub notifications can skip reconciliation.
    /// </summary>
    public double WebSubReliabilityThresholdHours { get; set; } = 6.0;
    
    /// <summary>
    /// Number of recent videos to analyze for calculating average publish interval.
    /// </summary>
    public int ActivityAnalysisSampleSize { get; set; } = 10;
    
    /// <summary>
    /// Activity threshold in days for high-activity channels (daily or more frequent).
    /// </summary>
    public double HighActivityThresholdDays { get; set; } = 1.0;
    
    /// <summary>
    /// Activity threshold in days for medium-activity channels (weekly).
    /// </summary>
    public double MediumActivityThresholdDays { get; set; } = 7.0;
    
    /// <summary>
    /// Activity threshold in days for low-activity channels (monthly).
    /// </summary>
    public double LowActivityThresholdDays { get; set; } = 30.0;
    
    /// <summary>
    /// Default interval in days for channels with insufficient data.
    /// </summary>
    public double DefaultInactiveIntervalDays { get; set; } = 30.0;
}
