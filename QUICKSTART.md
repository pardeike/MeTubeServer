# Quick Start Guide

Get the MeTube Hub Server running in 5 minutes!

## Prerequisites

- .NET 9.0 SDK ([Download](https://dotnet.microsoft.com/download/dotnet/9.0))
- YouTube Data API key ([Get one here](https://console.cloud.google.com/apis/credentials))
- For production: A public HTTPS URL (use ngrok for local testing)

## 5-Minute Setup

### Step 1: Clone and Configure

```bash
git clone https://github.com/pardeike/MeTubeServer.git
cd MeTubeServer
```

### Step 2: Set Your Configuration

Edit `appsettings.json`:

```json
{
  "YouTube": {
    "ApiKey": "YOUR_YOUTUBE_API_KEY_HERE",
    "CallbackBaseUrl": "https://your-server-url.com"
  }
}
```

Or use environment variables:

```bash
export YouTube__ApiKey="YOUR_YOUTUBE_API_KEY"
export YouTube__CallbackBaseUrl="https://your-server-url.com"
```

### Step 3: Run!

```bash
dotnet run
```

That's it! The server is now running at `http://localhost:5000`

## Quick Test

### 1. Check Health

```bash
curl http://localhost:5000/health
```

Expected response:
```json
{
  "status": "healthy",
  "timestamp": "2025-12-06T12:00:00Z",
  "stats": { "channels": 0, "users": 0, "videos": 0 }
}
```

### 2. Register a Channel

```bash
curl -X POST http://localhost:5000/api/users/testuser/channels \
  -H "Content-Type: application/json" \
  -d '{"channelIds": ["UCXuqSBlHAE6Xw-yeJA0Tunw"]}'
```

### 3. Get the Feed

```bash
curl http://localhost:5000/api/users/testuser/feed
```

## Using Docker

Even faster with Docker:

```bash
# Copy environment file
cp .env.example .env

# Edit .env with your values
nano .env

# Run with docker-compose
docker-compose up -d

# Check logs
docker-compose logs -f
```

## Local Testing with ngrok

For WebSub to work locally, you need a public HTTPS URL:

```bash
# Terminal 1: Start the server
dotnet run

# Terminal 2: Start ngrok
ngrok http 5000

# Update your configuration with the ngrok URL
export YouTube__CallbackBaseUrl="https://abc123.ngrok.io"

# Restart the server
```

## Next Steps

- Read [README.md](README.md) for architecture details
- Check [EXAMPLES.md](EXAMPLES.md) for more usage examples
- See [CONTRIBUTING.md](CONTRIBUTING.md) if you want to contribute

## Troubleshooting

### "Database connection failed"
The database is auto-created on first run. If you see this error, check file permissions.

### "WebSub verification failed"
Ensure your `CallbackBaseUrl` is:
- Publicly accessible via HTTPS
- Not blocked by a firewall
- Correctly set in configuration

### "YouTube API quota exceeded"
Check your quota in [Google Cloud Console](https://console.cloud.google.com/apis/dashboard).
The default free tier is 10,000 units/day, which should be plenty.

## Support

- Open an issue: https://github.com/pardeike/MeTubeServer/issues
- Read the docs: [README.md](README.md)
- Check examples: [EXAMPLES.md](EXAMPLES.md)

---

**Happy coding! 🚀**
