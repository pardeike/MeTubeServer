# MeTube Hub Server

A .NET 9 companion server for the MeTube app that efficiently manages YouTube channel subscriptions and video metadata through WebSub push notifications and periodic reconciliation.

## Overview

The MeTube Hub Server acts as a middleware between YouTube and the MeTube app to:
- Minimize YouTube API quota usage by caching channel and video data
- Receive push notifications from YouTube via WebSub for real-time updates
- Provide fast, aggregated "new videos" feeds to MeTube clients
- Support multiple users with shared channel subscriptions

## Architecture

### Data Flow
```
YouTube → WebSub Hub → MeTube Hub Server → MeTube App
                     ↑                    ↓
                  YouTube Data API    (caching)
```

### Key Features
- **WebSub Push Notifications**: Real-time video updates via YouTube's PubSubHubbub
- **Automatic Subscription Management**: Maintains WebSub subscriptions with lease renewal
- **Reconciliation**: Periodic polling to backfill missed notifications
- **Multi-user Support**: One WebSub subscription per channel, shared across users
- **HMAC Security**: Verifies WebSub notifications with shared secrets

## Technology Stack

- **.NET 9.0** - ASP.NET Core Minimal APIs
- **Entity Framework Core 9.0** - ORM with SQLite provider
- **SQLite** - Local database (easily swappable with PostgreSQL)
- **YouTube Data API v3** - Channel and video metadata

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- YouTube Data API key ([Get one here](https://console.cloud.google.com/apis/credentials))
- Public HTTPS endpoint for WebSub callbacks

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=metubeserver.db"
  },
  "YouTube": {
    "ApiKey": "YOUR_YOUTUBE_API_KEY",
    "HubUrl": "https://pubsubhubbub.appspot.com/subscribe",
    "CallbackBaseUrl": "https://your-server-url.com"
  },
  "Hub": {
    "WebSubSharedSecretLength": 32
  }
}
```

### Environment Variables

You can override configuration via environment variables:
- `YouTube__ApiKey` - Your YouTube Data API key
- `YouTube__CallbackBaseUrl` - Public URL where this server is accessible
- `ConnectionStrings__DefaultConnection` - Database connection string

## Installation

### Local Development

1. Clone the repository:
```bash
git clone https://github.com/pardeike/MeTubeServer.git
cd MeTubeServer
```

2. Configure the application:
```bash
# Edit appsettings.json or set environment variables
export YouTube__ApiKey="YOUR_API_KEY"
export YouTube__CallbackBaseUrl="https://your-ngrok-url.com"
```

3. Run the application:
```bash
dotnet run
```

### Docker

```bash
docker build -t metubeserver .
docker run -p 5000:8080 \
  -e YouTube__ApiKey="YOUR_API_KEY" \
  -e YouTube__CallbackBaseUrl="https://your-domain.com" \
  metubeserver
```

Or use docker-compose (recommended):

```bash
cp .env.example .env
# Edit .env with your values
docker-compose up -d
```

### Cloudflare Tunnel (Home Server)

1. Install Cloudflare Tunnel:
```bash
cloudflared tunnel login
cloudflared tunnel create metubeserver
```

2. Run the server with tunnel:
```bash
cloudflared tunnel --url http://localhost:5000 run metubeserver
```

## API Endpoints

### Health Check

#### `GET /health`
Health check endpoint for monitoring server status.

**Response (Healthy):**
```json
{
  "status": "healthy",
  "timestamp": "2025-12-06T12:00:00Z",
  "stats": {
    "channels": 10,
    "users": 5,
    "videos": 250
  }
}
```

**Response (Unhealthy):**
```json
{
  "status": "unhealthy",
  "message": "Database connection failed"
}
```

### WebSub Callbacks

#### `GET /websub/youtube`
WebSub verification endpoint. Called by YouTube's hub to verify subscriptions.

**Query Parameters:**
- `hub.mode` - "subscribe" or "unsubscribe"
- `hub.topic` - YouTube feed URL
- `hub.challenge` - Challenge string to echo back
- `hub.lease_seconds` - Subscription lease duration

**Response:** Returns the challenge string in plain text.

#### `POST /websub/youtube`
WebSub notification endpoint. Receives Atom feed notifications when new videos are published.

**Headers:**
- `X-Hub-Signature` or `X-Hub-Signature-256` - HMAC signature for verification

**Body:** Atom XML feed with video entries

### App Integration

#### `POST /api/users/{appUserId}/channels`
Register channel subscriptions for a user.

**Request Body:**
```json
{
  "channelIds": ["UC123...", "UC456..."]
}
```

**Response:**
```json
{
  "message": "Channels registered successfully"
}
```

#### `GET /api/users/{appUserId}/feed`
Get the user's aggregated video feed.

**Query Parameters:**
- `since` (optional) - ISO 8601 timestamp, returns videos published after this time
- `limit` (optional, default: 50) - Maximum number of videos to return

**Response:**
```json
{
  "videos": [
    {
      "videoId": "abc123",
      "channelId": "UC123...",
      "publishedAt": "2025-12-06T12:00:00Z",
      "title": "Video Title",
      "description": "Video description...",
      "thumbnailUrl": "https://...",
      "duration": "00:04:13"
    }
  ],
  "nextPageToken": null
}
```

## Data Model

### Entities

- **Channel**: YouTube channels with WebSub subscription info
- **User**: MeTube app users
- **UserChannel**: Many-to-many relationship between users and channels
- **Video**: Cached video metadata

### Database Schema

```
Channels
  - Id (PK)
  - ChannelId (unique)
  - TopicUrl (unique)
  - UploadsPlaylistId
  - HubSecret
  - LeaseExpiresAt
  - LastSeenPublishedAt

Users
  - Id (PK)
  - AppUserId (unique)

UserChannels
  - UserId (FK)
  - ChannelId (FK)
  - UserLastSeenPublishedAt

Videos
  - Id (PK)
  - VideoId (unique)
  - ChannelId (FK)
  - PublishedAt
  - Title
  - Description
  - ThumbnailUrl
  - Duration
```

## Background Jobs

### Subscription Maintenance Job
- **Frequency**: Every hour
- **Purpose**: Renews WebSub subscriptions before they expire
- **Logic**: Renews subscriptions that expire within 24 hours

### Reconciliation Job
- **Frequency**: Every 30 minutes
- **Purpose**: Backfills videos missed during downtime or WebSub failures
- **Logic**: Queries YouTube Data API for recent uploads per channel

## Deployment Options

### Option A: Cloudflare Workers (Recommended)
- Globally distributed edge computing
- Minimal latency
- Free tier available
- Cloudflare D1 for database

### Option B: Home Server + Cloudflare Tunnel
- Full control over infrastructure
- Easy debugging
- SQLite database on disk
- Protected by Cloudflare Tunnel

### Option C: Cloud VPS
- Any cloud provider (AWS, Azure, GCP, DigitalOcean, etc.)
- PostgreSQL or SQLite database
- Docker deployment recommended

## Quota Management

The server minimizes YouTube API quota usage:
- **WebSub subscriptions**: No quota cost (push-based)
- **channels.list**: 1 unit per channel (one-time per channel)
- **playlistItems.list**: 1 unit per call (periodic reconciliation only)
- **videos.list**: 1 unit per call (batched up to 50 video IDs)

Estimated quota for 100 channels with 5 videos/day: ~500 units/day (well under the 10,000 daily free tier).

## Logging

The server logs:
- WebSub verification requests and notifications
- Channel subscription/unsubscription events
- Video additions and updates
- YouTube API errors
- HMAC verification failures

Log levels can be configured in `appsettings.json`.

## Security

- **HMAC Verification**: All WebSub notifications are verified using HMAC-SHA1/SHA256
- **No User Credentials**: Server never stores user OAuth tokens
- **Public Endpoint**: Only public YouTube data is cached
- **Rate Limiting**: Consider adding rate limiting for production use

## Troubleshooting

### WebSub notifications not received
1. Verify `YouTube__CallbackBaseUrl` is publicly accessible via HTTPS
2. Check server logs for verification requests
3. Ensure firewall allows incoming HTTPS traffic
4. Verify WebSub hub can reach your callback URL

### Videos missing from feed
1. Check reconciliation job logs
2. Verify YouTube API key is valid and has quota
3. Check `LastSeenPublishedAt` timestamps in database
4. Run reconciliation manually to backfill

### Database errors
1. Ensure write permissions for SQLite database file
2. Check connection string in configuration
3. Verify database file exists and is not corrupted

## Development

### Building
```bash
dotnet build
```

### Running Tests (TODO)
```bash
dotnet test
```

### Database Reset
```bash
rm metubeserver.db
dotnet run
```

## Contributing

Contributions are welcome! Please open an issue or pull request.

## License

See [LICENSE](LICENSE) file for details.

## Related Projects

- [MeTube iOS App](https://github.com/pardeike/MeTube) - The companion iOS app

## Support

For issues and questions, please open an issue on GitHub.
