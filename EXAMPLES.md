# MeTube Hub Server - Usage Examples

## Quick Start

### 1. Set up Configuration

Create a `.env` file from the example:
```bash
cp .env.example .env
```

Edit `.env` and add your values:
```bash
YOUTUBE_API_KEY=AIzaSy...your_key_here
CALLBACK_BASE_URL=https://your-ngrok-url.com  # or your production URL
```

### 2. Run with Docker Compose

```bash
docker-compose up -d
```

The server will be available at `http://localhost:5000`

### 3. Test Locally with ngrok (Development)

If testing locally, you need a public HTTPS URL for WebSub callbacks:

```bash
# Install ngrok
brew install ngrok  # macOS
# or download from https://ngrok.com/

# Start the server
dotnet run

# In another terminal, start ngrok
ngrok http 5000

# Update your .env with the ngrok URL
CALLBACK_BASE_URL=https://abc123.ngrok.io
```

## API Usage Examples

### Register User's Channels

When a user opens the MeTube app and has subscribed channels:

```bash
curl -X POST http://localhost:5000/api/users/user123/channels \
  -H "Content-Type: application/json" \
  -d '{
    "channelIds": [
      "UCXuqSBlHAE6Xw-yeJA0Tunw",
      "UC_x5XG1OV2P6uZZ5FSM9Ttw"
    ]
  }'
```

**Response:**
```json
{
  "message": "Channels registered successfully"
}
```

### Get User's Video Feed

Retrieve all new videos for a user:

```bash
# Get all recent videos
curl http://localhost:5000/api/users/user123/feed

# Get videos published after a specific time
curl "http://localhost:5000/api/users/user123/feed?since=2025-12-01T00:00:00Z"

# Limit results
curl "http://localhost:5000/api/users/user123/feed?limit=10"
```

**Response:**
```json
{
  "videos": [
    {
      "videoId": "dQw4w9WgXcQ",
      "channelId": "UCXuqSBlHAE6Xw-yeJA0Tunw",
      "publishedAt": "2025-12-06T10:30:00Z",
      "title": "Example Video Title",
      "description": "Video description...",
      "thumbnailUrl": "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
      "duration": "00:04:13"
    }
  ],
  "nextPageToken": null
}
```

## WebSub Flow

### How WebSub Works

1. **User registers channels** via `POST /api/users/{userId}/channels`
2. **Server subscribes to WebSub** for each new channel
3. **YouTube's hub verifies** via `GET /websub/youtube`
4. **Server receives notifications** via `POST /websub/youtube` when new videos are published
5. **App polls feed** via `GET /api/users/{userId}/feed` to get new videos

### Testing WebSub Locally

You can manually test the WebSub verification endpoint:

```bash
# Simulate a hub verification request
curl "http://localhost:5000/websub/youtube?hub.mode=subscribe&hub.topic=https://www.youtube.com/feeds/videos.xml?channel_id=UCXuqSBlHAE6Xw-yeJA0Tunw&hub.challenge=test123&hub.lease_seconds=864000"
```

**Expected Response:** `test123` (the challenge echoed back)

## Integration with MeTube App

### Typical Flow

1. **App Startup**:
   - User opens MeTube app
   - App authenticates with Google OAuth
   - App calls `subscriptions.list(mine=true)` to get user's channels
   - App sends channel IDs to hub: `POST /api/users/{userId}/channels`

2. **Fetching Videos**:
   - App calls `GET /api/users/{userId}/feed?since={lastSyncTime}`
   - Hub returns aggregated videos from all subscribed channels
   - App caches the response and updates UI

3. **Background Sync**:
   - App periodically calls the feed endpoint when foregrounded
   - Only new videos since last sync are returned

### Example iOS Integration (Swift)

```swift
// Register user's channels
func registerChannels(userId: String, channelIds: [String]) async throws {
    let url = URL(string: "https://your-hub-server.com/api/users/\(userId)/channels")!
    var request = URLRequest(url: url)
    request.httpMethod = "POST"
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    
    let body = ["channelIds": channelIds]
    request.httpBody = try JSONEncoder().encode(body)
    
    let (_, response) = try await URLSession.shared.data(for: request)
    guard (response as? HTTPURLResponse)?.statusCode == 200 else {
        throw NSError(domain: "MeTube", code: -1)
    }
}

// Fetch video feed
func fetchFeed(userId: String, since: Date?) async throws -> [Video] {
    var urlString = "https://your-hub-server.com/api/users/\(userId)/feed"
    if let since = since {
        let iso8601 = ISO8601DateFormatter().string(from: since)
        urlString += "?since=\(iso8601)"
    }
    
    let url = URL(string: urlString)!
    let (data, _) = try await URLSession.shared.data(from: url)
    
    let response = try JSONDecoder().decode(FeedResponse.self, from: data)
    return response.videos
}
```

## Deployment Examples

### Deploy to DigitalOcean (Docker)

```bash
# SSH into your droplet
ssh root@your-droplet-ip

# Clone the repository
git clone https://github.com/pardeike/MeTubeServer.git
cd MeTubeServer

# Create .env file
nano .env
# Add your YOUTUBE_API_KEY and CALLBACK_BASE_URL

# Start the server
docker-compose up -d

# Check logs
docker-compose logs -f
```

### Deploy with Cloudflare Tunnel

```bash
# Start the server locally
dotnet run

# In another terminal, start Cloudflare Tunnel
cloudflared tunnel --url http://localhost:5000 run metubeserver

# The tunnel URL will be your CALLBACK_BASE_URL
# Update appsettings.json with this URL
```

### Deploy to Azure Container Apps

```bash
# Build and push Docker image
docker build -t youracr.azurecr.io/metubeserver:latest .
docker push youracr.azurecr.io/metubeserver:latest

# Deploy container app
az containerapp create \
  --name metubeserver \
  --resource-group myResourceGroup \
  --environment myEnvironment \
  --image youracr.azurecr.io/metubeserver:latest \
  --target-port 8080 \
  --ingress external \
  --env-vars \
    "YouTube__ApiKey=$YOUTUBE_API_KEY" \
    "YouTube__CallbackBaseUrl=https://metubeserver.azurecontainerapps.io"
```

## Monitoring and Debugging

### Check Server Health

```bash
# View logs
docker-compose logs -f

# Or if running directly
dotnet run --urls "http://localhost:5000"
```

### Verify Database

```bash
# Install sqlite3
sqlite3 data/metubeserver.db

# Check channels
SELECT * FROM Channels;

# Check videos
SELECT COUNT(*) FROM Videos;

# Check subscriptions
SELECT u.AppUserId, c.ChannelId 
FROM UserChannels uc
JOIN Users u ON uc.UserId = u.Id
JOIN Channels c ON uc.ChannelId = c.Id;
```

### Test WebSub Subscription

```bash
# Check if subscriptions are active
sqlite3 data/metubeserver.db "SELECT ChannelId, LeaseExpiresAt FROM Channels;"

# Force subscription renewal (restart the server or wait for the maintenance job)
```

## Troubleshooting

### WebSub notifications not received

**Problem**: Videos are not appearing in the feed immediately.

**Solution**:
1. Ensure your `CallbackBaseUrl` is publicly accessible via HTTPS
2. Check server logs for verification requests
3. Verify the reconciliation job is running (check logs every 30 minutes)
4. Test the callback URL manually: `curl https://your-server.com/websub/youtube?hub.mode=subscribe&hub.topic=...&hub.challenge=test`

### YouTube API quota exceeded

**Problem**: Server logs show 403 errors from YouTube API.

**Solution**:
1. Check your quota usage in Google Cloud Console
2. Reduce reconciliation frequency in `ReconciliationJob.cs`
3. WebSub notifications don't use quota, so this only affects reconciliation

### Database locked errors

**Problem**: SQLite database is locked.

**Solution**:
1. Ensure only one instance of the server is running
2. Check for long-running transactions
3. Consider migrating to PostgreSQL for production use

## Advanced Configuration

### Use PostgreSQL Instead of SQLite

Update `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=metubeserver;Username=postgres;Password=yourpassword"
  }
}
```

Update `MeTubeServer.csproj`:
```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.0" />
```

Update `Program.cs`:
```csharp
builder.Services.AddDbContext<MeTubeDbContext>(options =>
    options.UseNpgsql(connectionString));
```

### Customize Background Job Intervals

Edit `BackgroundJobs/SubscriptionMaintenanceJob.cs`:
```csharp
private readonly TimeSpan _interval = TimeSpan.FromMinutes(30); // Changed from 1 hour
```

Edit `BackgroundJobs/ReconciliationJob.cs`:
```csharp
private readonly TimeSpan _interval = TimeSpan.FromHours(1); // Changed from 30 minutes
```

## Performance Tips

1. **Database Indexing**: The entities already have indexes on key fields
2. **Connection Pooling**: EF Core handles this automatically
3. **Batch Processing**: Video metadata enrichment is batched (50 videos per API call)
4. **Caching**: Consider adding Redis for feed response caching in high-traffic scenarios

## Security Best Practices

1. **API Key**: Never commit your YouTube API key to version control
2. **HTTPS Only**: Always use HTTPS for the callback URL in production
3. **Rate Limiting**: Add rate limiting middleware for production deployments
4. **Authentication**: Consider adding API key authentication for the app endpoints
5. **HMAC Verification**: Already implemented for WebSub notifications

## Next Steps

- Add authentication for app endpoints
- Implement video metadata enrichment job
- Add telemetry and monitoring (Application Insights, Prometheus)
- Create health check endpoints
- Add unit and integration tests
- Implement pagination for large feeds
- Add support for video watch history sync
