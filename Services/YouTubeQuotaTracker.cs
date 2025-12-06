namespace MeTubeServer.Services;

/// <summary>
/// Service for tracking YouTube API quota usage.
/// YouTube API has a quota limit of 10,000 units per day.
/// </summary>
public class YouTubeQuotaTracker
{
    private readonly ILogger<YouTubeQuotaTracker> _logger;
    private int _quotaUsedToday = 0;
    private DateOnly _quotaDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly object _lock = new();
    private const int DailyQuotaLimit = 10000;

    public YouTubeQuotaTracker(ILogger<YouTubeQuotaTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Records quota usage for a YouTube API operation.
    /// </summary>
    /// <param name="operation">The operation performed (e.g., "channels.list", "videos.list")</param>
    /// <param name="units">Number of quota units consumed by this operation</param>
    public void RecordQuotaUsage(string operation, int units)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _quotaDate)
            {
                // New day, reset quota
                _quotaUsedToday = 0;
                _quotaDate = today;
                _logger.LogInformation("YouTube API quota reset for new day: {Date}", today);
            }

            _quotaUsedToday += units;
            
            var percentUsed = (_quotaUsedToday * 100.0) / DailyQuotaLimit;
            
            if (_quotaUsedToday >= DailyQuotaLimit)
            {
                _logger.LogWarning("YouTube API quota limit reached! Used: {QuotaUsed}/{QuotaLimit} units", 
                    _quotaUsedToday, DailyQuotaLimit);
            }
            else if (percentUsed >= 80)
            {
                _logger.LogWarning("YouTube API quota usage high: {QuotaUsed}/{QuotaLimit} units ({Percent:F1}%) - Operation: {Operation}", 
                    _quotaUsedToday, DailyQuotaLimit, percentUsed, operation);
            }
            else
            {
                _logger.LogDebug("YouTube API quota usage: {QuotaUsed}/{QuotaLimit} units ({Percent:F1}%) - Operation: {Operation}", 
                    _quotaUsedToday, DailyQuotaLimit, percentUsed, operation);
            }
        }
    }

    /// <summary>
    /// Gets the current quota usage for today.
    /// </summary>
    /// <returns>Number of quota units used today</returns>
    public int GetQuotaUsedToday()
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _quotaDate)
            {
                return 0;
            }
            return _quotaUsedToday;
        }
    }

    /// <summary>
    /// Gets the remaining quota for today.
    /// </summary>
    /// <returns>Number of quota units remaining</returns>
    public int GetRemainingQuota()
    {
        return Math.Max(0, DailyQuotaLimit - GetQuotaUsedToday());
    }

    /// <summary>
    /// Checks if there is sufficient quota remaining for an operation.
    /// </summary>
    /// <param name="requiredUnits">Number of quota units required</param>
    /// <returns>True if sufficient quota is available</returns>
    public bool HasSufficientQuota(int requiredUnits)
    {
        return GetRemainingQuota() >= requiredUnits;
    }
}
