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

## 3. On-Demand Reconciliation (User-Triggered)

**Cost: 1 unit per channel, only when user requests**

### Call Chain
```
POST /api/users/{appUserId}/reconcile (Program.cs)
  ↓
ReconciliationService.ReconcileUserChannelsAsync(userId)
  ↓ For each user's channel:
ReconciliationService.ReconcileChannelAsync(channel)
  ↓
YouTubeApiService.GetPlaylistItemsAsync(uploadsPlaylistId)
  ↓
YouTube API: playlistItems.list - 1 unit per channel
```

### Details
- **When**: Only when user pulls to refresh or app comes to foreground
- **Why**: Catches videos missed by WebSub (backup mechanism)
- **Quota**: 1 unit × number of user's channels
- **Frequency**: User-controlled (typically 1-10 times per day per user)

### Daily Impact Examples
Assuming users refresh 5 times per day on average:
- 10 channels, 10 users: 500 units/day (5%)
- 50 channels, 20 users: 5,000 units/day (50%)
- 100 channels, 50 users: 25,000 units/day → **exceeds limit** ⚠️

**Key advantage**: Only reconciles channels for active users, not all channels constantly.

### Optimization Options
1. **Rate limit reconciliation** - Max 1 reconciliation per user per 15 minutes
2. **Throttle per user** - Limit to specific number of reconciliation requests per day
3. **Smart reconciliation** - Skip channels that received WebSub notifications recently
4. **Background fallback** - Add daily reconciliation for channels not accessed in 24 hours

### Code Location
- API endpoint: `Program.cs` lines 636-671 (new)
- Service: `Services/ReconciliationService.cs` (new)
- API call: `ReconciliationService.ReconcileChannelAsync()` line 99

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
| `GetPlaylistItemsAsync()` | User reconciliation | playlistItems.list | 1 unit | Per user refresh × channels | **User-dependent** |
| `GetVideosDetailsAsync()` | Video discovery | videos.list | 1 unit | Per 50 videos | ~10-20 units |
| WebSub notification | YouTube push | (none) | 0 units | Per video | 0 units |

**Note**: Reconciliation is now on-demand (user-triggered) instead of automatic background polling.

---

## Total Daily Quota Calculation

```
Daily Quota Used = 
  100 × (number of restarts today)
  + 2 × (new channels added today)
  + (user reconciliation requests × avg channels per user)
  + ceiling(new videos today / 50)
```

### Example: 100 channels (10 users), 500 videos/day, 1 restart, users refresh 5× each
```
= (100 × 1) + 0 + (10 users × 5 refreshes × 10 channels/user) + ceiling(500/50)
= 100 + 0 + 500 + 10
= 610 units (6.1% of 10,000 limit) ✅
```

### Example: 200 channels (20 users), 1000 videos/day, 2 restarts, users refresh 10× each
```
= (100 × 2) + 0 + (20 users × 10 refreshes × 10 channels/user) + ceiling(1000/50)
= 200 + 0 + 2,000 + 20
= 2,220 units (22.2% of 10,000 limit) ✅
```

**Dramatic improvement**: With on-demand reconciliation, quota usage is much lower and scales with active users rather than total channels.

---

## High-Impact Optimization Recommendations

Listed by potential quota savings:

### 1. On-Demand Reconciliation (IMPLEMENTED - HIGHEST IMPACT) ✅
- **Change**: Removed automatic ReconciliationJob, added user-triggered API endpoint
- **Savings**: 90-95% of reconciliation quota
- **Before**: 48N units/day (where N = total channels)
- **After**: (users × refreshes × channels/user) units/day
- **Example**: 200 channels, was 9,600 units/day → now ~2,000 units/day with 20 active users
- **Trade-off**: Users may miss videos if they don't refresh, but WebSub provides real-time updates
- **Location**: `POST /api/users/{appUserId}/reconcile`, `Services/ReconciliationService.cs`

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

1. **On-demand reconciliation** (✅ IMPLEMENTED): `POST /api/users/{appUserId}/reconcile`
   - App calls this endpoint on pull-to-refresh or foreground transition
   - Reconciles only the user's channels, not all channels

2. **Rate limit reconciliation**: `Program.cs` line 671
   - Add rate limiting to prevent excessive reconciliation requests
   - Example: Max 1 reconciliation per user per 15 minutes

3. **Startup validation**: `Program.cs` lines 160-173 or `Services/YouTubeApiService.cs` lines 62-91
   - Comment out validation or switch to cheaper API call

4. **Channel metadata**: `Program.cs` lines 564-592
   - Combine the two API calls into one with `part=contentDetails,snippet`

5. **Video enrichment**: `Services/ReconciliationService.cs` lines 146-153
   - Comment out enrichment task queueing (lose duration info)

---

## Questions to Ask

Based on this analysis, you might want to consider:

1. **Is reconciliation necessary?** If WebSub is reliable, you could reduce frequency or disable it
2. **Is startup validation worth 100 units?** Consider cheaper alternatives or caching
3. **Do you need video duration immediately?** Could delay or skip enrichment
4. **Can you batch channel registration?** If users import many channels at once
5. **What's the acceptable trade-off** between quota usage and data freshness?
