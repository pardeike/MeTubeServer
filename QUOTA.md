# YouTube API Quota Usage Guide

This document explains how MeTube Server uses YouTube API quota and how to monitor and manage it effectively.

## Overview

YouTube Data API v3 provides a **daily quota of 10,000 units** for free tier projects. Each API operation consumes a different number of quota units. MeTube Server is designed to minimize quota usage through caching and WebSub push notifications, but understanding quota consumption is important for scaling and troubleshooting.

## Quota Costs by Operation

### Server Operations

| Operation | Cost (units) | When Used | Frequency |
|-----------|-------------|-----------|-----------|
| `search.list` | 100 | API key validation at startup | Once per startup |
| `channels.list` (contentDetails) | 1 | Get uploads playlist ID | Once per new channel |
| `channels.list` (snippet) | 1 | Get channel metadata (name, thumbnail) | Once per new channel |
| `playlistItems.list` | 1 | Check for new videos (reconciliation) | Every 30 min per channel |
| `videos.list` | 1 | Get video details (duration, metadata) | Per batch of up to 50 videos |

### WebSub Subscriptions

**Important**: WebSub push notifications **do not use any quota**. This is the primary mechanism for receiving new video notifications in real-time, making the server extremely quota-efficient.

## Daily Quota Calculation Examples

### Example 1: Small Server (10 channels, 2 videos/day each)
```
Startup:              100 units (one-time)
Reconciliation:       48 jobs/day × 10 channels = 480 units/day
Video enrichment:     20 videos/day ÷ 50 per batch = 1 unit/day
New channel overhead: ~2 units (occasional)
──────────────────────────────────────────────────
Total:                ~583 units/day (5.8% of limit)
```

### Example 2: Medium Server (100 channels, 5 videos/day each)
```
Startup:              100 units (one-time)
Reconciliation:       48 jobs/day × 100 channels = 4,800 units/day
Video enrichment:     500 videos/day ÷ 50 per batch = 10 units/day
New channel overhead: ~10 units (occasional)
──────────────────────────────────────────────────
Total:                ~4,920 units/day (49.2% of limit)
```

### Example 3: Large Server (200 channels, 5 videos/day each)
```
Startup:              100 units (one-time)
Reconciliation:       48 jobs/day × 200 channels = 9,600 units/day
Video enrichment:     1,000 videos/day ÷ 50 per batch = 20 units/day
New channel overhead: ~20 units (occasional)
──────────────────────────────────────────────────
Total:                ~9,740 units/day (97.4% of limit) ⚠️
```

## When Quota Limits Are Exceeded

### Common Scenarios

1. **Too Many Channels**
   - With 200+ channels, reconciliation alone can consume ~9,600 units/day
   - Solution: Reduce reconciliation frequency or request quota increase

2. **Frequent Server Restarts**
   - Each restart consumes 100 units for API validation
   - Multiple restarts in a day can add up quickly
   - Solution: Ensure stable deployment, use health checks to prevent unnecessary restarts

3. **New User Onboarding**
   - Each new channel costs 2 units (uploads playlist + metadata)
   - Bulk user imports can consume significant quota
   - Solution: Spread out channel additions or batch them efficiently

4. **Reconciliation Frequency**
   - Current default: every 30 minutes (48 times/day)
   - More frequent reconciliation = higher quota usage
   - Solution: Adjust reconciliation interval based on your needs

## Monitoring Quota Usage

### Health Check Endpoints

The server exposes quota information through health check endpoints:

#### GET `/health`
Returns basic health status including quota:
```json
{
  "status": "healthy",
  "timestamp": "2025-12-06T10:30:00Z",
  "stats": {
    "channels": 100,
    "users": 25,
    "videos": 5000
  },
  "quota": {
    "used": 4920,
    "remaining": 5080,
    "limit": 10000,
    "percentUsed": 49.2
  }
}
```

#### GET `/health/details`
Returns detailed health information including quota date:
```json
{
  "status": "healthy",
  "timestamp": "2025-12-06T10:30:00Z",
  "stats": {
    "channels": 100,
    "users": 25,
    "videos": 5000
  },
  "quota": {
    "used": 4920,
    "remaining": 5080,
    "limit": 10000,
    "percentUsed": 49.2,
    "date": "2025-12-06"
  }
}
```

### Server Logs

The server logs quota usage at different levels:

- **Debug**: Every API call (quota usage and percentage)
- **Warning**: When usage exceeds 80% of daily limit
- **Warning**: When daily limit is reached

Example log entries:
```
[DBG] YouTube API quota usage: 4500/10000 units (45.0%) - Operation: playlistItems.list
[WRN] YouTube API quota usage high: 8200/10000 units (82.0%) - Operation: channels.list
[WRN] YouTube API quota limit reached! Used: 10000/10000 units
[INF] YouTube API quota reset for new day: 2025-12-07
```

## Optimization Strategies

### 1. Rely on WebSub (No Quota Cost)
- WebSub push notifications are real-time and use zero quota
- Ensure WebSub subscriptions are working properly
- Check that your callback URL is publicly accessible via HTTPS

### 2. Adjust Reconciliation Frequency
The reconciliation job is the largest quota consumer. Consider adjusting the interval in `BackgroundJobs/ReconciliationJob.cs`:

```csharp
// Current: 30 minutes (48 times/day)
private readonly TimeSpan _interval = TimeSpan.FromMinutes(30);

// Option 1: 60 minutes (24 times/day) - cuts quota usage in half
private readonly TimeSpan _interval = TimeSpan.FromHours(1);

// Option 2: 2 hours (12 times/day) - reduces to 25% of current usage
private readonly TimeSpan _interval = TimeSpan.FromHours(2);
```

**Trade-off**: Longer intervals mean you may miss videos for longer if WebSub fails, but significantly reduce quota usage.

### 3. Batch Operations
- Video enrichment already batches up to 50 videos per API call
- Channel metadata fetching is batched efficiently
- These are already optimized

### 4. Request Quota Increase
If you need more than 10,000 units/day:

1. Go to [Google Cloud Console - YouTube Data API Quotas](https://console.cloud.google.com/apis/api/youtube.googleapis.com/quotas)
2. Select your project
3. Click "APPLY FOR HIGHER QUOTA"
4. Fill out the quota increase request form explaining your use case
5. Wait for approval (typically 1-3 business days)

### 5. Use Multiple API Keys (Advanced)
For very large deployments, you can:
- Create multiple Google Cloud projects with different API keys
- Distribute channels across different API keys
- This requires code modifications and is not currently supported

## Troubleshooting

### Quota Exceeded Error
**Symptom**: Server logs show 403 errors with "quotaExceeded" message

**Solutions**:
1. Check current usage: `curl http://localhost:5000/health`
2. Review server logs to identify which operations are consuming quota
3. Increase reconciliation interval to reduce quota usage
4. Wait for daily reset (occurs at midnight Pacific Time)
5. Request quota increase from Google

### Server Fails to Start Due to Quota
**Symptom**: Server fails during startup with quota check failure

**Solutions**:
1. The startup validation uses 100 units - ensure you have at least 100 units available
2. Wait for daily quota reset
3. Temporarily disable startup validation (not recommended for production)
4. Request quota increase

### Unexpected High Quota Usage
**Investigation steps**:
1. Check `/health/details` endpoint to see current usage
2. Review server logs for patterns:
   ```bash
   # Check for high-cost operations
   grep "quota usage" /var/log/metube-server.log | grep -E "(search|playlistItems)"
   
   # Count API calls by type
   grep "YouTube API quota" /var/log/metube-server.log | \
     awk '{print $NF}' | sort | uniq -c
   ```
3. Identify if specific operations are being called more frequently than expected
4. Check for server restart loops (each restart = 100 units)

## Best Practices

1. **Monitor Regularly**: Check `/health` endpoint regularly to track quota usage trends
2. **Set Up Alerts**: Create alerts when quota usage exceeds 80%
3. **WebSub First**: Always ensure WebSub is working - it's your zero-quota notification system
4. **Plan for Growth**: Calculate your expected quota needs before scaling up channels
5. **Test in Development**: Use a separate API key for development to avoid impacting production quota
6. **Document Restarts**: Keep track of server restarts as each consumes 100 units
7. **Batch When Possible**: The server already does this, but be aware when adding custom features

## API Key Management

### Separate Keys for Environments
Use different API keys for:
- **Development**: For testing and development work
- **Staging**: For pre-production testing
- **Production**: For live server usage

This prevents development/testing from consuming production quota.

### Rotating API Keys
If you need to rotate API keys:
1. Create a new API key in Google Cloud Console
2. Update the `YouTube:ApiKey` configuration
3. Restart the server (this will consume 100 units for validation)
4. Old key can be disabled after confirming new key works

## Further Resources

- [YouTube Data API Quota Calculator](https://developers.google.com/youtube/v3/determine_quota_cost)
- [YouTube Data API Documentation](https://developers.google.com/youtube/v3)
- [Quota Management in Google Cloud Console](https://console.cloud.google.com/apis/api/youtube.googleapis.com/quotas)
- [Request Quota Increase Form](https://support.google.com/youtube/contact/yt_api_form)

## Summary

MeTube Server is designed to be quota-efficient through:
- **WebSub push notifications** (zero quota)
- **Intelligent caching** (minimal redundant API calls)
- **Batched operations** (up to 50 items per call)
- **Adjustable reconciliation** (balance between freshness and quota usage)

With proper configuration and monitoring, you can serve:
- **~180 channels** comfortably within free tier limits
- **More channels** by adjusting reconciliation frequency
- **200+ channels** with a quota increase request

Monitor your quota usage regularly through the `/health` endpoint and adjust your configuration as needed.
