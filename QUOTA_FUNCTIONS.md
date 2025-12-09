# Functions That Consume YouTube API Quota

This document lists every function in the codebase that consumes YouTube API quota, organized by call chain and context. Use this to identify optimization opportunities and workarounds.

---

## 1. Startup: API Key Validation

**Cost: 100 units per server restart**

### Call Chain
```
Program.cs (Startup, line 163)
  ↓
YouTubeApiService.ValidateApiKeyAsync()
  ↓
YouTube API: search.list (100 units)
```

### Details
- **When**: Every time the server starts
- **Why**: Validates the YouTube API key is working
- **Quota**: 100 units (search.list API call)
- **Frequency**: Once per restart

### Optimization Options
1. **Remove validation** (risky) - Skip startup check, fail later on actual use
2. **Use cheaper validation** - Switch to `channels.list` with a known channel ID (1 unit instead of 100)
3. **Cache validation** - Remember successful validation for X hours (requires persistent state)
4. **Conditional validation** - Only validate on first startup or configuration change

### Code Location
- `Program.cs` lines 160-173
- `YouTubeApiService.ValidateApiKeyAsync()` lines 62-91

---

## 2. New Channel Registration

**Cost: 2 units per channel (one-time)**

### Call Chain
```
POST /api/users/{appUserId}/channels (Program.cs line 564-592)
  ↓ Background task queue
  ↓
YouTubeApiService.GetUploadsPlaylistIdAsync(channelId)
  ↓
YouTube API: channels.list (part=contentDetails) - 1 unit
  
AND

YouTubeApiService.GetChannelMetadataAsync(channelId)
  ↓
YouTube API: channels.list (part=snippet) - 1 unit
```

### Details
- **When**: User adds a new channel to their subscriptions
- **Why**: Gets the uploads playlist ID and channel metadata (name, thumbnail)
- **Quota**: 2 units total (2 separate channels.list calls)
- **Frequency**: Once per unique channel across all users (shared channels don't repeat)

### Optimization Options
1. **Combine API calls** - Fetch both contentDetails and snippet in one call (`part=contentDetails,snippet`) - **Reduces to 1 unit total**
2. **Lazy load metadata** - Only fetch metadata on-demand when viewing channel details
3. **Skip uploads playlist** - If WebSub is reliable, you may not need reconciliation at all
4. **Batch new channels** - If user imports many channels, batch the API calls (up to 50 channels per request)

### Code Location
- User endpoint: `Program.cs` lines 438-612
- Background task: `Program.cs` lines 564-592
- `YouTubeApiService.GetUploadsPlaylistIdAsync()` lines 28-56
- `YouTubeApiService.GetChannelMetadataAsync()` lines 150-191

---

## 3. Reconciliation Job (Background)

**Cost: 1 unit per channel, runs every 30 minutes**

### Call Chain
```
ReconciliationJob.ExecuteAsync() (every 30 min)
  ↓
ReconciliationJob.ReconcileChannelsAsync()
  ↓ For each channel:
ReconciliationJob.ReconcileChannelAsync(channel)
  ↓
YouTubeApiService.GetPlaylistItemsAsync(uploadsPlaylistId)
  ↓
YouTube API: playlistItems.list - 1 unit per channel
```

### Details
- **When**: Every 30 minutes, for ALL channels
- **Why**: Catches videos missed by WebSub (backup mechanism)
- **Quota**: 1 unit × number of channels × 48 times per day
- **Frequency**: 48 times per day (every 30 minutes)

### Daily Impact Examples
- 10 channels: 480 units/day (4.8%)
- 50 channels: 2,400 units/day (24%)
- 100 channels: 4,800 units/day (48%)
- 200 channels: 9,600 units/day (96%) ⚠️

### Optimization Options
1. **Increase interval** - Change from 30 min to 1 hour = 50% reduction (24 runs/day)
2. **Increase interval** - Change from 30 min to 2 hours = 75% reduction (12 runs/day)
3. **Skip if WebSub working** - Only reconcile channels that haven't received WebSub notifications
4. **Stagger reconciliation** - Don't check all channels at once, spread over the interval
5. **Prioritize active channels** - Check frequently-updated channels more often
6. **User-based throttling** - Only reconcile channels with active users

### Code Location
- Job: `BackgroundJobs/ReconciliationJob.cs` lines 13, 25-82
- API call: `ReconciliationJob.cs` line 97
- `YouTubeApiService.GetPlaylistItemsAsync()` lines 200-239

---

## 4. Video Enrichment (Background)

**Cost: 1 unit per batch of up to 50 videos**

### Call Chain
```
Multiple triggers:
  a) WebSub notification → Program.cs websub handler → Background queue
  b) Reconciliation → ReconciliationJob → Background queue
  ↓ Background task queue
VideoEnrichmentService.EnrichVideoAsync(videoId)
  ↓
YouTubeApiService.GetVideosDetailsAsync([videoId])
  ↓
YouTube API: videos.list - 1 unit (batches up to 50 video IDs)
```

### Details
- **When**: After discovering a new video (via WebSub or reconciliation)
- **Why**: Gets video duration and full metadata
- **Quota**: 1 unit per batch (can include up to 50 videos)
- **Frequency**: Once per new video discovered

### Daily Impact Examples
- 100 videos/day: 2 units/day (0.02%)
- 500 videos/day: 10 units/day (0.1%)
- 1000 videos/day: 20 units/day (0.2%)

### Optimization Options
1. **Batch enrichment** - Already implemented! Use `EnrichVideosAsync()` instead of single-video enrichment
2. **Lazy enrichment** - Only enrich videos when user requests them
3. **Skip enrichment** - If duration isn't critical, skip this entirely
4. **Delayed enrichment** - Wait 1 hour and batch all new videos together

### Code Location
- Single video: `VideoEnrichmentService.EnrichVideoAsync()` lines 25-70
- Batch: `VideoEnrichmentService.EnrichVideosAsync()` lines 75-130
- API call: `YouTubeApiService.GetVideosDetailsAsync()` lines 241-277

---

## 5. WebSub Notifications (NO QUOTA)

**Cost: 0 units**

### Call Chain
```
POST /websub/youtube (Program.cs line 279)
  ↓
AtomFeedParser.Parse(body) - No API call
  ↓
Save video to database - No API call
  ↓
Background queue → VideoEnrichmentService (see #4 above)
```

### Details
- **When**: YouTube pushes notifications for new videos
- **Why**: Real-time updates without polling
- **Quota**: **0 units** - This is the free, push-based mechanism
- **Frequency**: Whenever a subscribed channel publishes a video

### Notes
- WebSub itself uses NO quota
- Only the follow-up video enrichment uses quota (1 unit per 50 videos)
- This is why WebSub is critical for staying within quota limits

### Code Location
- Handler: `Program.cs` lines 279-437

---

## Summary Table

| Function | Trigger | API Call | Cost | Frequency | Daily Impact |
|----------|---------|----------|------|-----------|--------------|
| `ValidateApiKeyAsync()` | Server startup | search.list | 100 units | Per restart | 100 units |
| `GetUploadsPlaylistIdAsync()` | New channel | channels.list | 1 unit | Per new channel | Variable |
| `GetChannelMetadataAsync()` | New channel | channels.list | 1 unit | Per new channel | Variable |
| `GetPlaylistItemsAsync()` | Reconciliation | playlistItems.list | 1 unit | 48× per day × N channels | **48N units** |
| `GetVideosDetailsAsync()` | Video discovery | videos.list | 1 unit | Per 50 videos | ~10-20 units |
| WebSub notification | YouTube push | (none) | 0 units | Per video | 0 units |

---

## Total Daily Quota Calculation

```
Daily Quota Used = 
  100 (startup, if restarted today)
  + 2 × (new channels added today)
  + 48 × (total channels)
  + ceiling(new videos today / 50)
```

### Example: 100 channels, 500 videos/day, 1 restart
```
= 100 + 0 + (48 × 100) + ceiling(500/50)
= 100 + 4,800 + 10
= 4,910 units (49.1% of 10,000 limit)
```

### Example: 200 channels, 1000 videos/day, 2 restarts
```
= 200 + 0 + (48 × 200) + ceiling(1000/50)
= 200 + 9,600 + 20
= 9,820 units (98.2% of 10,000 limit) ⚠️
```

---

## High-Impact Optimization Recommendations

Listed by potential quota savings:

### 1. Increase Reconciliation Interval (HIGHEST IMPACT)
- **Current**: 30 minutes (48× per day)
- **Change to**: 60 minutes (24× per day)
- **Savings**: 50% of reconciliation quota (24N units/day)
- **Trade-off**: Videos may be delayed up to 1 hour if WebSub fails
- **Location**: `ReconciliationJob.cs` line 13

### 2. Combine Channel Metadata Calls (MEDIUM IMPACT)
- **Current**: 2 API calls per new channel
- **Change to**: 1 API call with `part=contentDetails,snippet`
- **Savings**: 1 unit per new channel (50% reduction)
- **Trade-off**: None
- **Location**: `Program.cs` lines 564-592, or create new combined method

### 3. Use Cheaper API Key Validation (MEDIUM-LOW IMPACT)
- **Current**: search.list = 100 units
- **Change to**: channels.list with known ID = 1 unit
- **Savings**: 99 units per restart
- **Trade-off**: Slightly less thorough validation
- **Location**: `YouTubeApiService.ValidateApiKeyAsync()` lines 62-91

### 4. Conditional Reconciliation (VARIABLE IMPACT)
- **Current**: Check all channels every 30 min
- **Change to**: Skip channels that received WebSub notification recently
- **Savings**: Varies by WebSub reliability (potentially 50-80% of reconciliation)
- **Trade-off**: More complex logic
- **Location**: `ReconciliationJob.ReconcileChannelsAsync()` lines 55-82

### 5. Batch Video Enrichment (LOW IMPACT - Already efficient)
- **Current**: Already batches up to 50 videos per call
- **Optimization**: Delay enrichment by 5-10 minutes to increase batch size
- **Savings**: Minimal (already efficient)
- **Trade-off**: Slight delay in video duration appearing

---

## Quick Reference: Where to Edit

To reduce quota consumption, modify these files:

1. **Reconciliation frequency**: `BackgroundJobs/ReconciliationJob.cs` line 13
   - Change `TimeSpan.FromMinutes(30)` to `TimeSpan.FromHours(1)` or more

2. **Startup validation**: `Program.cs` lines 160-173 or `Services/YouTubeApiService.cs` lines 62-91
   - Comment out validation or switch to cheaper API call

3. **Channel metadata**: `Program.cs` lines 564-592
   - Combine the two API calls into one with `part=contentDetails,snippet`

4. **Video enrichment**: `BackgroundJobs/ReconciliationJob.cs` lines 146-153
   - Comment out enrichment task queueing (lose duration info)

---

## Questions to Ask

Based on this analysis, you might want to consider:

1. **Is reconciliation necessary?** If WebSub is reliable, you could reduce frequency or disable it
2. **Is startup validation worth 100 units?** Consider cheaper alternatives or caching
3. **Do you need video duration immediately?** Could delay or skip enrichment
4. **Can you batch channel registration?** If users import many channels at once
5. **What's the acceptable trade-off** between quota usage and data freshness?
