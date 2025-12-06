using System.Diagnostics.Metrics;

namespace MeTubeServer.Services;

/// <summary>
/// Service for tracking application metrics.
/// </summary>
public class MetricsService
{
    private readonly Meter _meter;
    private readonly Counter<long> _websubNotificationsReceived;
    private readonly Counter<long> _videosAdded;
    private readonly Counter<long> _channelsRegistered;
    private readonly Counter<long> _feedRequests;
    private readonly Histogram<double> _feedRequestDuration;

    public MetricsService(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create("MeTubeServer");
        
        _websubNotificationsReceived = _meter.CreateCounter<long>(
            "metube.websub.notifications.received",
            description: "Number of WebSub notifications received");
        
        _videosAdded = _meter.CreateCounter<long>(
            "metube.videos.added",
            description: "Number of new videos added to the database");
        
        _channelsRegistered = _meter.CreateCounter<long>(
            "metube.channels.registered",
            description: "Number of channels registered");
        
        _feedRequests = _meter.CreateCounter<long>(
            "metube.feed.requests",
            description: "Number of feed requests");
        
        _feedRequestDuration = _meter.CreateHistogram<double>(
            "metube.feed.duration",
            unit: "ms",
            description: "Duration of feed requests in milliseconds");
    }

    /// <summary>
    /// Records a WebSub notification received.
    /// </summary>
    public void RecordWebSubNotification(string channelId)
    {
        _websubNotificationsReceived.Add(1, 
            new KeyValuePair<string, object?>("channel", channelId));
    }

    /// <summary>
    /// Records a video added to the database.
    /// </summary>
    public void RecordVideoAdded(string channelId, string videoId)
    {
        _videosAdded.Add(1, 
            new KeyValuePair<string, object?>("channel", channelId),
            new KeyValuePair<string, object?>("video", videoId));
    }

    /// <summary>
    /// Records channels registered for a user.
    /// </summary>
    public void RecordChannelsRegistered(int count, string userId)
    {
        _channelsRegistered.Add(count, 
            new KeyValuePair<string, object?>("user", userId));
    }

    /// <summary>
    /// Records a feed request.
    /// </summary>
    public void RecordFeedRequest(string userId)
    {
        _feedRequests.Add(1, 
            new KeyValuePair<string, object?>("user", userId));
    }

    /// <summary>
    /// Records the duration of a feed request.
    /// </summary>
    public void RecordFeedRequestDuration(double durationMs, string userId)
    {
        _feedRequestDuration.Record(durationMs, 
            new KeyValuePair<string, object?>("user", userId));
    }
}
