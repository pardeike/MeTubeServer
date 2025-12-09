namespace MeTubeServer.Services;

/// <summary>
/// Service for tracking YouTube API availability status.
/// Detects quota exceeded conditions and periodically polls to check if quota is available again.
/// </summary>
public class YouTubeApiStatusService : IHostedService, IDisposable
{
    private readonly ILogger<YouTubeApiStatusService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly object _lock = new();
    
    private bool _isApiAvailable = true;
    private DateTimeOffset _quotaExceededAt = DateTimeOffset.MinValue;
    private Timer? _recoveryTimer;
    
    /// <summary>
    /// Interval to check if quota has been released (5 minutes).
    /// </summary>
    private readonly TimeSpan _recoveryCheckInterval = TimeSpan.FromMinutes(5);
    
    // Cache the Pacific Time zone since YouTube quota resets at midnight PT
    private static readonly TimeZoneInfo PacificTimeZone = 
        TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    public YouTubeApiStatusService(
        ILogger<YouTubeApiStatusService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Gets whether the YouTube API is currently available for use.
    /// </summary>
    public bool IsApiAvailable
    {
        get
        {
            lock (_lock)
            {
                return _isApiAvailable;
            }
        }
    }

    /// <summary>
    /// Records that a quota exceeded error was received from the YouTube API.
    /// </summary>
    public void RecordQuotaExceeded()
    {
        lock (_lock)
        {
            if (_isApiAvailable)
            {
                _isApiAvailable = false;
                _quotaExceededAt = DateTimeOffset.UtcNow;
                _logger.LogWarning("YouTube API quota exceeded. API calls will be suspended until quota is released. " +
                    "Quota resets at midnight Pacific Time.");
            }
        }
    }

    /// <summary>
    /// Records that the YouTube API is working (e.g., a successful response was received).
    /// </summary>
    public void RecordApiAvailable()
    {
        lock (_lock)
        {
            if (!_isApiAvailable)
            {
                _isApiAvailable = true;
                _logger.LogInformation("YouTube API quota restored. API calls are now enabled.");
            }
        }
    }

    /// <summary>
    /// Checks if there's been a day rollover since quota was exceeded (quota resets at midnight PT).
    /// </summary>
    public bool HasDayRolledOverSinceQuotaExceeded()
    {
        lock (_lock)
        {
            if (_isApiAvailable || _quotaExceededAt == DateTimeOffset.MinValue)
                return false;

            // Get the date when quota was exceeded in Pacific time
            var exceededPacificTime = TimeZoneInfo.ConvertTime(_quotaExceededAt, PacificTimeZone);
            var exceededDate = DateOnly.FromDateTime(exceededPacificTime.DateTime);

            // Get today's date in Pacific time
            var nowPacificTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTimeZone);
            var todayDate = DateOnly.FromDateTime(nowPacificTime);

            return todayDate > exceededDate;
        }
    }

    /// <summary>
    /// Gets the time when quota was exceeded.
    /// </summary>
    public DateTimeOffset? GetQuotaExceededAt()
    {
        lock (_lock)
        {
            if (_isApiAvailable)
                return null;
            return _quotaExceededAt;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("YouTube API Status Service started");
        
        // Start a timer to periodically check if quota has been released
        _recoveryTimer = new Timer(
            CheckQuotaRecovery, 
            null, 
            _recoveryCheckInterval, 
            _recoveryCheckInterval);
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("YouTube API Status Service stopped");
        _recoveryTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async void CheckQuotaRecovery(object? state)
    {
        // Only check if API is currently marked as unavailable
        if (IsApiAvailable)
            return;

        // Check if we've crossed into a new day (Pacific time) since quota was exceeded
        if (!HasDayRolledOverSinceQuotaExceeded())
        {
            _logger.LogDebug("YouTube API quota still exceeded. Quota resets at midnight Pacific Time.");
            return;
        }

        _logger.LogInformation("Day has rolled over since quota was exceeded. Checking if API is available...");

        try
        {
            // Create a scope to get the YouTubeApiService
            using var scope = _serviceProvider.CreateScope();
            var youtubeApi = scope.ServiceProvider.GetRequiredService<YouTubeApiService>();
            
            // Try to validate the API key - this will call the API
            // Pass skipStatusCheck: true to allow the API call even though we're marked as unavailable
            var isValid = await youtubeApi.ValidateApiKeyAsync(default, skipStatusCheck: true);
            
            if (isValid)
            {
                RecordApiAvailable();
            }
            else
            {
                _logger.LogDebug("YouTube API validation returned false. Quota may still be exceeded.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking YouTube API availability. Will retry later.");
        }
    }

    public void Dispose()
    {
        _recoveryTimer?.Dispose();
    }
}
