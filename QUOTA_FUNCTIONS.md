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

## 3. Adaptive Reconciliation (Client-Initiated or Optional Background)

**Cost: 1 unit per channel, with intelligent throttling**

### Call Chain
```
POST /api/users/{appUserId}/reconcile (Program.cs) [Client-Initiated - DEFAULT]
  OR
ReconciliationJob (Background - Optional, disabled by default)
  ↓
ReconciliationService.ReconcileUserChannelsAsync(userId)  [client-initiated]
  OR
ReconciliationService.ReconcileSingleChannelAsync(channel)  [background]
  ↓
YouTubeApiService.GetPlaylistItemsAsync(uploadsPlaylistId)
  ↓
YouTube API: playlistItems.list - 1 unit per channel
```

### Details
- **When**: 
  - **Default**: Only when user pulls to refresh or app comes to foreground
  - **Optional**: Background job (disabled by default, configurable via `EnableBackgroundReconciliation`)
- **Why**: Catches videos missed by WebSub (backup mechanism)
- **Quota**: 1 unit × number of channels reconciled
- **Frequency**: 
  - **Client-initiated**: User-controlled (typically 1-10 times per day per user)
  - **Background**: Adaptive based on channel activity (if enabled)

### Adaptive Throttling (NEW - Applies to Both Modes)
The system now intelligently throttles reconciliation based on channel publishing patterns:

**Channel Activity Levels:**
- **High-activity** (daily uploads): Max every 2 hours (12x/day)
- **Medium-activity** (weekly uploads): Max every 12 hours (2x/day)
- **Low-activity** (monthly uploads): Max every 2 days (0.5x/day)
- **Inactive** (30+ days between videos): Max every 7 days (0.14x/day)
- **WebSub reliable** (notification within 6 hours): Skip entirely (0x/day)
- **Dead channels** (inactive + WebSub working within 30 days): Skip entirely (0x/day)

**How it works:**
- System tracks `LastReconciledAt`, `AveragePublishInterval`, and `LastWebSubNotification` for each channel
- Calculates average publish frequency from last 10 videos (configurable)
- Channels with recent WebSub notifications (<6 hours) skip reconciliation
- **Inactive channels with working WebSub also skip** - only reconcile if WebSub hasn't fired in 30+ days
- This dramatically reduces quota usage for dead channels (which may be 80% of subscriptions)

**Dead Channel Optimization:**
For channels that haven't posted in 30+ days:
- If WebSub notification received within last 30 days: **skip reconciliation** (WebSub is working)
- If no WebSub activity in 30+ days: reconcile every 7 days (check if channel revived or WebSub broken)
- Configurable via `WebSubStaleThresholdDays` (set to 90+ days to be even more conservative)

### Daily Impact Examples

**Client-Initiated (Default):**
- 10 channels, 10 users: ~100-300 units/day (1-3%)
- 50 channels, 20 users: ~500-1,500 units/day (5-15%)
- 100 channels, 50 users: ~1,000-3,000 units/day (10-30%)

**Background Job (Optional, if enabled) - With 80% Dead Channels:**
- 200 channels (160 dead, 40 active): ~500-800 units/day (5-8%)
  - 160 dead channels with working WebSub: 0 units (skipped entirely)
  - 20 active with recent WebSub: 0 units
  - 10 high-activity: 120 units (12x/day)
  - 10 medium/low-activity: 100 units (mixed)

**Background Job (Optional, if enabled):**
- 200 channels: ~2,400 units/day (24%) with adaptive throttling
  - 100 channels skip via WebSub: 0 units
  - 20 high-activity: 240 units (12x/day)
  - 40 medium-activity: 800 units (2x/day)
  - 40 low-activity: 200 units (0.5x/day)

**Key advantages**: 
- Client-initiated: Zero quota when users inactive
- Adaptive throttling: 70-75% reduction vs fixed intervals
- WebSub skip: Additional 50% savings on channels with working push notifications

### Configuration Options
All thresholds configurable in `appsettings.json`:
```json
{
  "Hub": {
    "EnableBackgroundReconciliation": false,
    "ReconciliationBaseIntervalMinutes": 60,
    "ReconciliationHighActivityMultiplier": 2.0,
    "ReconciliationMediumActivityMultiplier": 12.0,
    "ReconciliationLowActivityMultiplier": 48.0,
    "ReconciliationInactiveMultiplier": 168.0,
    "WebSubReliabilityThresholdHours": 6.0,
    "WebSubStaleThresholdDays": 30.0,
    "ActivityAnalysisSampleSize": 10,
    "HighActivityThresholdDays": 1.0,
    "MediumActivityThresholdDays": 7.0,
    "LowActivityThresholdDays": 30.0
  }
}
```

**Key setting for dead channels**: Increase `WebSubStaleThresholdDays` to 90 or even 180 days to further reduce quota usage on channels that rarely/never post. This means dead channels with working WebSub won't be reconciled for 3-6 months, dramatically reducing quota for servers with many inactive subscriptions.

### Code Location
- API endpoint: `Program.cs` lines 665-702
- Background job: `BackgroundJobs/ReconciliationJob.cs`
- Service: `Services/ReconciliationService.cs`
- Adaptive logic: `ReconciliationJob.CalculateReconciliationInterval()` lines 188-242

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
| `GetPlaylistItemsAsync()` | Reconciliation | playlistItems.list | 1 unit | **Adaptive** | **Activity-dependent** |
| `GetVideosDetailsAsync()` | Video discovery | videos.list | 1 unit | Per 50 videos | ~10-20 units |
| WebSub notification | YouTube push | (none) | 0 units | Per video | 0 units |

**Note**: Reconciliation uses adaptive throttling based on channel activity, dramatically reducing quota usage. Background reconciliation is optional (disabled by default); client-initiated reconciliation is the default.

---

## Total Daily Quota Calculation

### Client-Initiated Mode (Default)
```
Daily Quota Used = 
  100 × (number of restarts today)
  + 2 × (new channels added today)
  + (active reconciliations with adaptive throttling)
  + ceiling(new videos today / 50)
```

**Active Reconciliations**: Only channels reconciled when users request it, subject to adaptive throttling:
- High-activity channels: max 12x/day
- Medium-activity channels: max 2x/day
- Low-activity channels: max 0.5x/day
- Channels with recent WebSub: 0x/day (skipped)

### Background Mode (Optional, if EnableBackgroundReconciliation=true)
```
Daily Quota Used = 
  100 × (number of restarts today)
  + 2 × (new channels added today)
  + (background reconciliation with adaptive throttling)
  + ceiling(new videos today / 50)
```

**Background Reconciliation**: All channels evaluated hourly, but reconciled based on activity:
- Varies by channel mix (typically 2,000-3,000 units/day for 200 channels)

### Example: 100 channels (10 users), 500 videos/day, 1 restart, client-initiated
```
Active users: 8 users × 5 refreshes/day = 40 reconciliation requests
Adaptive throttling applied per channel:
  - 50 channels skip (recent WebSub): 0 units
  - 20 high-activity allowed: 40 requests → 20 actual (throttled to 2h) = 20 units
  - 30 medium/low activity: 40 requests → 15 actual (throttled) = 15 units

= 100 + 0 + 35 + ceiling(500/50)
= 100 + 0 + 35 + 10
= 145 units (1.45% of 10,000 limit) ✅
```

### Example: 200 channels (20 users), 1000 videos/day, 2 restarts, background enabled
```
Background reconciliation with adaptive throttling:
  - 100 channels skip (recent WebSub): 0 units
  - 20 high-activity (12x/day): 240 units
  - 40 medium-activity (2x/day): 80 units
  - 40 low-activity (0.5x/day): 20 units

= (100 × 2) + 0 + (240 + 80 + 20) + ceiling(1000/50)
= 200 + 0 + 340 + 20
= 560 units (5.6% of 10,000 limit) ✅
```

**Dramatic improvement**: Adaptive reconciliation reduces quota usage by 70-75% compared to fixed 30-minute intervals, while maintaining video freshness through WebSub and smart polling.

---

## High-Impact Optimization Recommendations

Listed by potential quota savings:

### 1. Adaptive Reconciliation with Client-Initiated Default (IMPLEMENTED - HIGHEST IMPACT) ✅
- **Change**: Implemented adaptive throttling based on channel activity + client-initiated by default
- **Savings**: 70-95% of reconciliation quota
- **Before**: 48N units/day (where N = total channels) with fixed 30-min intervals
- **After**: 
  - Client-initiated (default): ~100-500 units/day for typical usage
  - Background (optional): ~2,000-3,000 units/day for 200 channels with adaptive throttling
- **Features**:
  - Tracks channel publishing patterns (daily/weekly/monthly/inactive)
  - Skips channels with recent WebSub notifications (<6h)
  - Adjusts reconciliation frequency per channel (2h to 7 days)
  - Client-initiated mode: zero quota when users inactive
- **Configuration**: `EnableBackgroundReconciliation: false` (default), all thresholds configurable
- **Location**: `BackgroundJobs/ReconciliationJob.cs`, `Services/ReconciliationService.cs`, `Models/Options.cs`

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

### 4. WebSub Reliability Tracking (IMPLEMENTED - HIGH IMPACT) ✅
- **Change**: Track WebSub notification timestamps and skip reconciliation for reliable channels
- **Savings**: 50% of reconciliation quota (channels with working WebSub)
- **How**: Channels receiving WebSub notifications within 6 hours skip reconciliation
- **Exception**: Inactive channels always reconciled to detect activity changes
- **Location**: `ReconciliationJob.ShouldSkipDueToWebSub()`, `Channel.LastWebSubNotification`

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
