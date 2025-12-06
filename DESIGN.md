# JOB: Implement a .NET 9 “Hub” server for MeTube

## Purpose

Build a small .NET 9 server that acts as a hub between YouTube and the MeTube app:

- Data flow: `YouTube  →  Hub  →  MeTube App`
- The **app** is responsible for:
  - Logging into YouTube via OAuth.
  - Calling YouTube’s Data API with the user’s credentials (e.g. to list subscriptions).
- The **hub** is responsible for:
  - Minimizing YouTube quota usage by caching channel + video data.
  - Receiving push notifications from YouTube (WebSub) and reconciling with the Data API.
  - Providing a fast, aggregated “new videos” API for the MeTube app.
- Both sides cache aggressively:
  - “(1) YouTube → Hub”: Hub caches channel metadata + latest videos.
  - “(2) Hub → App”: App caches responses from the hub to avoid repeated hub calls and reduce startup time.

The main scenario is a single power user using MeTube many times per day, with potential for multiple users later.

---

## High-level architecture

Implement a .NET 9 ASP.NET Core minimal API service with:

- HTTP endpoints for:
  - WebSub callbacks from YouTube.
  - MeTube app integration (registering subscriptions, fetching new videos).
- Background jobs for:
  - Renewing WebSub subscriptions (leases).
  - Reconciling missed updates via YouTube Data API.
- Persistent storage (start with SQLite; keep DB abstraction simple so it can be swapped for Postgres later).

### Tech stack

- Target framework: `net9.0`
- ASP.NET Core minimal APIs.
- EF Core for persistence (SQLite provider initially).
- `HttpClient` (typed or named clients) for YouTube Data API calls.
- Background services via `IHostedService` / `PeriodicTimer`.

---

## External flows

### 1. MeTube App → Hub

The app:

1. Authenticates against Google/YouTube directly and obtains tokens.
2. Calls YouTube Data API to fetch the user’s subscriptions (channel IDs).
3. Sends these channel IDs to the hub:
   - `POST /api/users/{userId}/channels` with a JSON body listing channel IDs.
4. Fetches the user’s aggregated “new videos” feed from:
   - `GET /api/users/{userId}/feed?since=...`

The hub **never** stores user Google tokens. It only sees opaque `userId` (app-local) and public `channelId`s.

### 2. YouTube → Hub (WebSub + Data API)

For each subscribed channel, the hub:

1. Subscribes to YouTube’s WebSub hub:
   - `hub.mode=subscribe`
   - `hub.topic=https://www.youtube.com/feeds/videos.xml?channel_id={channelId}`
   - `hub.callback={ServerBaseUrl}/websub/youtube`
   - `hub.secret={randomHmacSecret}`
   - `hub.lease_seconds` (optional)
2. Receives verification requests (GET) and notifications (POST) at `/websub/youtube`.
3. On notification:
   - Verifies HMAC using the shared secret (if present).
   - Parses the Atom payload, extracts:
     - `yt:channelId`
     - `yt:videoId`
     - `published`
   - De-duplicates videos and updates internal state.
   - Optionally calls YouTube Data API (`videos.list`) in batches to enrich video metadata.

To handle downtime or missed notifications, a periodic reconciliation job fetches recent uploads per channel via `playlistItems.list` on the channel’s uploads playlist.

---

## Data model (entities)

Use EF Core entities (simplified schema; feel free to refine):

### Channel

```csharp
class Channel
{
    public int Id { get; set; }                      // Internal PK
    public string ChannelId { get; set; } = null!;   // YouTube channel ID, unique
    public string? UploadsPlaylistId { get; set; }   // From channels.list(contentDetails)
    public string TopicUrl { get; set; } = null!;    // WebSub topic URL
    public string? HubSecret { get; set; }           // HMAC secret for WebSub
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset? LastSeenPublishedAt { get; set; } // Latest video publish date seen by hub
    public ICollection<UserChannel> UserChannels { get; set; } = new List<UserChannel>();
}
```

### User

```csharp
class User
{
    public int Id { get; set; }                      // Internal PK
    public string AppUserId { get; set; } = null!;   // Stable ID from MeTube app
    public ICollection<UserChannel> UserChannels { get; set; } = new List<UserChannel>();
}
```

### UserChannel (many-to-many)

```csharp
class UserChannel
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;

    public DateTimeOffset? UserLastSeenPublishedAt { get; set; }
    // Used to compute “new for this user” relative to hub’s knowledge of the channel.
}
```

### Video (optional but recommended)

```csharp
class Video
{
    public int Id { get; set; }                      // Internal PK
    public string VideoId { get; set; } = null!;     // YouTube video ID, unique
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;

    public DateTimeOffset PublishedAt { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public TimeSpan? Duration { get; set; }          // From videos.list(contentDetails)
}
```

Data model goals:

- Minimize YouTube calls by:
  - Caching `UploadsPlaylistId` per channel.
  - Caching `Video` metadata after first fetch.
- Support multiple users following the same channel without duplicating WebSub subscriptions.

---

## Configuration

Use `IConfiguration` / options pattern with environment variables:

- `Youtube:ApiKey` – YouTube Data API key for server-side calls (no OAuth).
- `Youtube:HubUrl` – WebSub hub URL (e.g. Google’s PubSubHubbub hub).
- `Youtube:CallbackBaseUrl` – Public base URL for this server (used to compose callback).
- `Hub:WebSubSharedSecretLength` – Length of the random HMAC secret per channel.
- DB connection string (SQLite or other).

---

## HTTP endpoints

Implement these endpoints as minimal APIs.

### 1. WebSub callbacks

#### `GET /websub/youtube`

Used by the hub to verify subscription/unsubscription.

Query parameters:

- `hub.mode`
- `hub.topic`
- `hub.challenge`
- `hub.lease_seconds` (optional)

Behavior:

1. Look up the `Channel` by `hub.topic` (exact match).
2. If found and `hub.mode == "subscribe"`:
   - Update `LeaseExpiresAt = UtcNow + lease_seconds` (if provided).
3. If `hub.mode == "unsubscribe"`:
   - Either mark channel as unsubscribed or clear `LeaseExpiresAt`.
4. Return `200 OK` with response body exactly equal to `hub.challenge`.

#### `POST /websub/youtube`

Receives Atom notifications.

Behavior:

1. Read the raw body.
2. Verify HMAC if `HubSecret` exists:
   - Check signature header (e.g. `X-Hub-Signature` / `X-Hub-Signature-256`).
   - If invalid → `401` or `400`.
3. Parse Atom feed:
   - For each `<entry>`:
     - Extract `yt:channelId`, `yt:videoId`, `published`.
4. For each video:
   - Ensure `Channel` exists; if not, create a dormant record (no users yet).
   - If `PublishedAt <= LastSeenPublishedAt` for that channel, or `VideoId` already exists → skip.
   - Insert/update `Video` and update `LastSeenPublishedAt` for the channel.
5. Optionally enqueue a background work item to call `videos.list` in batch to enrich metadata.

Idempotency:

- `Video.VideoId` should be unique.
- Updates are safe to run multiple times.

### 2. App integration

#### `POST /api/users/{appUserId}/channels`

Body:

```json
{
  "channelIds": ["UC123...", "UC456...", "..."]
}
```

Behavior:

1. Upsert a `User` row with `AppUserId`.
2. For each `channelId` in the list:
   - If `Channel` does not exist:
     - Create a new `Channel` with:
       - `ChannelId`
       - `TopicUrl = "https://www.youtube.com/feeds/videos.xml?channel_id=" + channelId`
       - `HubSecret` = random secret
     - Persist to DB.
     - Trigger a WebSub subscribe request for this topic.
     - Trigger an async fetch to resolve `UploadsPlaylistId` via `channels.list`.
   - Upsert a `UserChannel` row linking this user to the channel.
3. Optionally detect channels previously followed by this user but not present in the new list and remove them.

This endpoint is idempotent; sending the same list again should not create duplicates.

#### `GET /api/users/{appUserId}/feed`

Query parameters:

- `since` (optional, ISO8601 timestamp) – “I only want videos published after this point”.
- `limit` / `pageToken` (optional) – for paging.

Behavior:

1. Look up the `User` by `AppUserId`.
2. Determine the effective `since`:
   - If `since` provided: use it.
   - Else: use per-user `UserLastSeenPublishedAt` or default to a reasonable window (e.g. last N days).
3. Query `Videos`:
   - `WHERE ChannelId IN (user’s channels)` and `PublishedAt > since`.
   - Order by `PublishedAt DESC`.
   - Apply paging.
4. Return a JSON DTO with:
   - `videos` (list of: videoId, channelId, publishedAt, title, etc.)
   - `nextPageToken` (if paging is used).
5. Optionally update each `UserChannel.UserLastSeenPublishedAt` to the max `PublishedAt` returned (or expose a separate endpoint for “mark as seen”).

---

## Background jobs

Implement as hosted services.

### 1. WebSub subscription maintenance

Periodic task (e.g. every hour):

1. Query channels where `LeaseExpiresAt` is null or `LeaseExpiresAt < UtcNow + SafetyMargin`.
2. For each such channel:
   - Send a WebSub `subscribe` request:
     - `hub.mode=subscribe`
     - `hub.topic=Channel.TopicUrl`
     - `hub.callback={CallbackBaseUrl}/websub/youtube`
     - `hub.secret=Channel.HubSecret`
   - The hub will call back the GET endpoint to confirm and update `LeaseExpiresAt`.

`SafetyMargin` should be smaller than the hub’s typical lease duration (e.g. 1 day before expiry).

### 2. Reconciliation job (YouTube Data API)

Periodic task (e.g. every N minutes/hours):

1. For each `Channel` with `UploadsPlaylistId` known:
   - Determine `since = LastSeenPublishedAt` minus a small overlap (e.g. 5 minutes to account for clock skew).
   - Call YouTube Data API `playlistItems.list`:
     - `playlistId = UploadsPlaylistId`
     - `maxResults` small (e.g. 10–20).
   - For each returned item:
     - Extract `videoId`, `publishedAt`.
     - If `PublishedAt > LastSeenPublishedAt` and `videoId` not yet stored:
       - Insert `Video`, update `LastSeenPublishedAt`.
2. Batch API calls reasonably and respect quota.

This job is the safety net that backfills any videos missed due to downtime or WebSub delivery issues.

---

## Quota and caching strategy

- **Avoid `search.list`** on the server; it’s expensive and unnecessary here.
- Use:
  - `channels.list(part=contentDetails)` once per channel to get `UploadsPlaylistId`, then cache it.
  - `playlistItems.list` only for reconciliation in small windows.
  - `videos.list` only when necessary to enrich metadata, with up to 50 video IDs per call.
- The hub becomes the central cache:
  - For a given channel, the server queries YouTube once, then serves all MeTube clients from its own DB.
  - Only one WebSub subscription per channel, regardless of number of users.

---

## Non-functional requirements

- All HTTP handlers should be async and non-blocking.
- Use DI for:
  - DB context
  - YouTube API client
  - WebSub subscription service
  - Background jobs
- Log:
  - WebSub verification calls and notifications (at least at info level).
  - All errors in WebSub HMAC verification or Atom parsing.
  - YouTube API call failures, including quota errors.
- Design for running both:
  - As a small container at home behind Cloudflare Tunnel, and
  - As a cloud-hosted service (e.g. Cloudflare Workers equivalent or a small Linux container).

The output of this job should be a working .NET 9 solution with:

- A minimal API project implementing the endpoints and background jobs above.
- EF Core migrations for the described schema.
- Configuration via `appsettings.json` + environment variables.
- Basic logging and error handling.
