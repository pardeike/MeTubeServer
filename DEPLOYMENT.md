# MeTube Hub Server - Deployment Guide

## Purpose

This comprehensive guide walks through deploying the MeTube Hub Server in two scenarios:
1. **Home deployment** with optional Cloudflare Tunnel
2. **Cloud deployment** using Cloudflare

The guide assumes basic Cloudflare account familiarity but provides detailed steps for all configurations.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Getting a YouTube API Key](#getting-a-youtube-api-key)
3. [Home Deployment](#home-deployment)
4. [Cloudflare Tunnel Setup](#cloudflare-tunnel-setup)
5. [Cloud Deployment with Cloudflare](#cloud-deployment-with-cloudflare)
6. [Configuration](#configuration)
7. [Verification and Testing](#verification-and-testing)
8. [Maintenance](#maintenance)
9. [Troubleshooting](#troubleshooting)

---

## Prerequisites

### Required

- **YouTube Data API key** (free, see next section)
- **Cloudflare account** (free tier is sufficient)
- **Docker and Docker Compose** installed (for home deployment)
  - OR **Git and .NET 9 SDK** if running directly

### Recommended

- **Domain name** managed by Cloudflare (optional but recommended for cloud deployment)
- **Basic terminal/command line knowledge**
- **Text editor** for configuration files

### System Requirements

**Home Server**:
- 2 GB RAM minimum (4 GB recommended)
- 10 GB disk space
- Linux, macOS, or Windows with WSL2
- Always-on internet connection (for real-time WebSub notifications)

**Cloud**:
- Cloudflare Workers (covered by free tier)
- Cloudflare D1 database (covered by free tier)

---

## Getting a YouTube API Key

### Step 1: Create Google Cloud Project

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Click **Select a project** → **New Project**
3. Enter project name: `MeTube Hub` (or any name)
4. Click **Create**
5. Wait for project creation (30 seconds)

### Step 2: Enable YouTube Data API v3

1. In the search bar, type: `YouTube Data API v3`
2. Click on **YouTube Data API v3**
3. Click **Enable**
4. Wait for activation (10-30 seconds)

### Step 3: Create API Key

1. Go to [Credentials](https://console.cloud.google.com/apis/credentials)
2. Click **Create Credentials** → **API Key**
3. Your API key will be displayed: `AIzaSy...` (keep this secret!)
4. Click **Edit API key**

### Step 4: Restrict API Key (Important!)

**Application restrictions**:
- Select **HTTP referrers (web sites)**
- Add: `https://your-server-domain.com/*` (your actual domain)
- Add: `http://localhost:5000/*` (for testing)

**API restrictions**:
- Select **Restrict key**
- Choose **YouTube Data API v3**
- Click **Save**

### Step 5: Verify Quota

1. Go to [Quotas page](https://console.cloud.google.com/apis/api/youtube.googleapis.com/quotas)
2. You should see: **10,000 units per day** (free tier)
3. This is sufficient for ~1000 channels with moderate activity

**💡 Tip**: Keep your API key safe! Store it in a password manager.

---

## Home Deployment

### Option A: Docker Compose (Recommended)

#### Step 1: Clone Repository

```bash
# Clone the repository
git clone https://github.com/pardeike/MeTubeServer.git
cd MeTubeServer

# Or download and extract ZIP from GitHub
```

#### Step 2: Create Configuration

```bash
# Copy the example environment file
cp .env.example .env

# Edit the file
nano .env  # or vim, or any text editor
```

Edit `.env` with your values:
```env
# Your YouTube Data API key from previous section
YOUTUBE_API_KEY=AIzaSy...your_key_here

# For now, use localhost (we'll update this for Cloudflare Tunnel later)
CALLBACK_BASE_URL=http://localhost:5000
```

**⚠️ Important**: 
- `CALLBACK_BASE_URL` MUST be publicly accessible via HTTPS for WebSub to work
- For testing locally, use `http://localhost:5000` but WebSub won't work
- For production, you'll need Cloudflare Tunnel (see next section)

#### Step 3: Start the Server

```bash
# Start in background
docker-compose up -d

# View logs
docker-compose logs -f

# Stop when needed
docker-compose down
```

#### Step 4: Verify It's Running

```bash
# Check health endpoint
curl http://localhost:5000/health

# Expected response:
# {"status":"healthy","timestamp":"2025-12-06T...","stats":{...}}
```

**✅ Success**: Your server is now running locally on port 5000!

### Option B: Direct .NET Execution

If you prefer not to use Docker:

#### Step 1: Install .NET 9

```bash
# macOS (with Homebrew)
brew install dotnet@9

# Ubuntu/Debian
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 9.0

# Windows: Download from https://dotnet.microsoft.com/download/dotnet/9.0
```

#### Step 2: Clone and Configure

```bash
git clone https://github.com/pardeike/MeTubeServer.git
cd MeTubeServer
```

Edit `appsettings.json`:
```json
{
  "YouTube": {
    "ApiKey": "AIzaSy...your_key_here",
    "CallbackBaseUrl": "http://localhost:5000"
  }
}
```

#### Step 3: Run the Server

```bash
# Build and run
dotnet run

# Or build release version
dotnet build -c Release
dotnet bin/Release/net9.0/MeTubeServer.dll

# Server will start on http://localhost:5000
```

---

## Cloudflare Tunnel Setup

Cloudflare Tunnel provides a secure way to expose your home server to the internet without opening firewall ports or configuring your router.

### Why Cloudflare Tunnel?

- ✅ No port forwarding needed
- ✅ No firewall configuration
- ✅ Automatic HTTPS
- ✅ DDoS protection
- ✅ Free

### Step 1: Install Cloudflared

#### macOS (Homebrew)
```bash
brew install cloudflare/cloudflare/cloudflared
```

#### Linux (Debian/Ubuntu)
```bash
wget -q https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64.deb
sudo dpkg -i cloudflared-linux-amd64.deb
```

#### Windows
Download from: https://github.com/cloudflare/cloudflared/releases/latest

### Step 2: Authenticate with Cloudflare

```bash
cloudflared tunnel login
```

This will:
1. Open your browser
2. Ask you to select a domain (if you have multiple)
3. Authorize the tunnel
4. Save credentials to `~/.cloudflared/cert.pem`

**💡 Note**: If you don't have a domain, you can get one from Cloudflare Registrar or another provider and point it to Cloudflare's nameservers.

### Step 3: Create a Tunnel

```bash
# Create a tunnel named "metubeserver"
cloudflared tunnel create metubeserver

# This creates a tunnel ID and credentials file
# Output: Created tunnel metubeserver with id abc-123-def-456
```

Copy the tunnel ID for later use.

### Step 4: Create Tunnel Configuration

Create `~/.cloudflared/config.yml`:

```yaml
tunnel: abc-123-def-456  # Replace with your tunnel ID
credentials-file: /home/youruser/.cloudflared/abc-123-def-456.json

ingress:
  - hostname: metube.yourdomain.com  # Replace with your domain
    service: http://localhost:5000
  - service: http_status:404
```

**Important**: Replace:
- `abc-123-def-456` with your actual tunnel ID
- `/home/youruser/` with your actual home directory path
- `metube.yourdomain.com` with your desired subdomain

### Step 5: Create DNS Record

```bash
# Add DNS record pointing to the tunnel
cloudflared tunnel route dns metubeserver metube.yourdomain.com
```

Or manually in Cloudflare Dashboard:
1. Go to your domain's DNS settings
2. Add a CNAME record:
   - Name: `metube`
   - Target: `abc-123-def-456.cfargotunnel.com` (use your tunnel ID)
   - Proxy status: Proxied (orange cloud)

### Step 6: Update Server Configuration

Edit your `.env` file:
```env
CALLBACK_BASE_URL=https://metube.yourdomain.com
```

Restart the server:
```bash
docker-compose down
docker-compose up -d
```

### Step 7: Start the Tunnel

```bash
# Start tunnel in foreground (for testing)
cloudflared tunnel run metubeserver

# Start tunnel in background
cloudflared tunnel run metubeserver &

# Or install as a service (recommended for production)
sudo cloudflared service install
sudo systemctl start cloudflared
sudo systemctl enable cloudflared
```

### Step 8: Verify Tunnel

```bash
# Test from outside your network (use a phone or ask a friend)
curl https://metube.yourdomain.com/health

# Should return: {"status":"healthy",...}
```

**✅ Success**: Your home server is now publicly accessible via HTTPS!

### Tunnel Management

```bash
# List tunnels
cloudflared tunnel list

# Check tunnel status
cloudflared tunnel info metubeserver

# View tunnel logs
journalctl -u cloudflared -f

# Stop tunnel
sudo systemctl stop cloudflared

# Delete tunnel (careful!)
cloudflared tunnel delete metubeserver
```

---

## Cloud Deployment with Cloudflare

Cloudflare offers serverless compute through Workers and databases through D1. However, the current MeTube Hub Server is built with .NET, which doesn't run directly on Cloudflare Workers.

### Options for Cloud Deployment

#### Option 1: Cloudflare Workers + Fly.io/Railway (Recommended)

Use a hybrid approach:
- **Database**: Cloudflare D1 (free tier)
- **Compute**: Fly.io or Railway (low-cost, supports Docker)
- **CDN/DDoS**: Cloudflare proxies traffic

**Why this approach?**
- Cloudflare Workers don't support .NET
- But you can use their edge network for DDoS protection
- Fly.io/Railway provide cheap, globally distributed Docker hosting

#### Option 2: Fly.io with Cloudflare in Front

Deploy Docker container to Fly.io, use Cloudflare for DNS and SSL.

### Deploying to Fly.io

#### Step 1: Install Fly CLI

```bash
# macOS
brew install flyctl

# Linux
curl -L https://fly.io/install.sh | sh

# Windows
pwsh -Command "iwr https://fly.io/install.ps1 -useb | iex"
```

#### Step 2: Sign Up and Login

```bash
# Sign up (opens browser)
flyctl auth signup

# Or login if you have an account
flyctl auth login
```

#### Step 3: Create Fly App

```bash
# Navigate to your MeTubeServer directory
cd /path/to/MeTubeServer

# Initialize Fly app
flyctl launch
```

This will:
1. Detect the Dockerfile
2. Ask for an app name (e.g., `metubeserver`)
3. Select a region (choose closest to your users)
4. Ask about PostgreSQL (choose **No**, we're using SQLite)

#### Step 4: Configure Secrets

```bash
# Set YouTube API key as secret
flyctl secrets set YOUTUBE_API_KEY="AIzaSy...your_key_here"

# Set callback URL (use your Fly.io app URL)
flyctl secrets set CALLBACK_BASE_URL="https://metubeserver.fly.dev"
```

#### Step 5: Configure Persistent Storage

Edit `fly.toml` (created by `flyctl launch`):

```toml
app = "metubeserver"
primary_region = "lax"  # Your selected region

[build]

[http_service]
  internal_port = 8080
  force_https = true
  auto_stop_machines = false  # Keep running for WebSub
  auto_start_machines = true
  min_machines_running = 1

[mounts]
  source = "metube_data"
  destination = "/app/data"
  initial_size = "1gb"

[[vm]]
  memory = '512mb'
  cpu_kind = 'shared'
  cpus = 1
```

#### Step 6: Create Volume

```bash
# Create persistent volume for SQLite database
flyctl volumes create metube_data --region lax --size 1
```

#### Step 7: Deploy

```bash
# Deploy the application
flyctl deploy

# This will:
# 1. Build the Docker image
# 2. Push to Fly.io registry
# 3. Deploy to your app
# 4. Start the container
```

#### Step 8: Verify Deployment

```bash
# Check status
flyctl status

# View logs
flyctl logs

# Test health endpoint
curl https://metubeserver.fly.dev/health
```

#### Step 9: Connect Cloudflare (Optional)

To add Cloudflare's DDoS protection and CDN:

1. In Fly.io Dashboard:
   - Get your app's IP address: `flyctl ips list`

2. In Cloudflare Dashboard:
   - Add A record: `metube` → `[Fly.io IP]` (Proxied)
   - Add AAAA record: `metube` → `[Fly.io IPv6]` (Proxied)

3. Update Fly.io secrets:
   ```bash
   flyctl secrets set CALLBACK_BASE_URL="https://metube.yourdomain.com"
   ```

4. In Cloudflare SSL/TLS settings:
   - Set SSL mode to **Full** (not Full Strict, since Fly.io uses self-signed cert internally)

### Deploying to Railway

Railway is another excellent Docker hosting platform with a generous free tier.

#### Step 1: Sign Up

Go to [railway.app](https://railway.app) and sign up with GitHub.

#### Step 2: Create New Project

1. Click **New Project**
2. Choose **Deploy from GitHub repo**
3. Select `MeTubeServer` repository
4. Railway will auto-detect the Dockerfile

#### Step 3: Add Environment Variables

In Railway Dashboard → Variables:
```
YOUTUBE_API_KEY=AIzaSy...your_key_here
CALLBACK_BASE_URL=https://metubeserver-production-xxxx.up.railway.app
```

(Railway will provide the URL after deployment)

#### Step 4: Add Volume

1. Go to **Settings** → **Volumes**
2. Click **Add Volume**
3. Mount path: `/app/data`
4. Size: 1 GB

#### Step 5: Deploy

1. Click **Deploy**
2. Wait for build and deployment (2-5 minutes)
3. Railway will provide a public URL

#### Step 6: Update Callback URL

Once you have the Railway URL:
```
CALLBACK_BASE_URL=https://metubeserver-production-xxxx.up.railway.app
```

Redeploy to pick up the change.

### Using Cloudflare Workers (Advanced)

**Note**: This requires rewriting the server in JavaScript/TypeScript. The current .NET implementation cannot run on Workers.

If you want a pure Cloudflare solution:

1. **Port to TypeScript**: Rewrite endpoints using Hono or similar framework
2. **Use D1**: Replace SQLite with Cloudflare D1
3. **Use Workers**: Deploy to Cloudflare Workers
4. **Use Cron Triggers**: For background jobs

This is a significant undertaking and not covered in this guide.

---

## Configuration

### Environment Variables

Complete reference of configuration options:

```env
# Required
YOUTUBE_API_KEY=AIzaSy...
CALLBACK_BASE_URL=https://your-domain.com

# Optional
ConnectionStrings__DefaultConnection=Data Source=/app/data/metubeserver.db
Hub__WebSubSharedSecretLength=32
```

### appsettings.json

If not using environment variables:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=/app/data/metubeserver.db"
  },
  "YouTube": {
    "ApiKey": "YOUR_API_KEY_HERE",
    "HubUrl": "https://pubsubhubbub.appspot.com/subscribe",
    "CallbackBaseUrl": "https://your-domain.com"
  },
  "Hub": {
    "WebSubSharedSecretLength": 32
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### Database Location

**Docker**: `/app/data/metubeserver.db` (persisted via volume)
**Local**: `./metubeserver.db` (in project directory)

To change location, update connection string:
```env
ConnectionStrings__DefaultConnection=Data Source=/custom/path/metubeserver.db
```

### Logging Configuration

To increase logging verbosity for debugging:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information",
      "MeTubeServer": "Debug"
    }
  }
}
```

---

## Verification and Testing

### Step 1: Health Check

```bash
curl https://your-domain.com/health
```

Expected response:
```json
{
  "status": "healthy",
  "timestamp": "2025-12-06T12:00:00Z",
  "stats": {
    "channels": 0,
    "users": 0,
    "videos": 0
  }
}
```

### Step 2: Register a Test Channel

```bash
curl -X POST https://your-domain.com/api/users/test-user/channels \
  -H "Content-Type: application/json" \
  -d '{
    "channelIds": ["UCXuqSBlHAE6Xw-yeJA0Tunw"]
  }'
```

Expected response:
```json
{
  "message": "Channels registered successfully"
}
```

### Step 3: Wait for WebSub Verification

Check server logs for:
```
WebSub verification: mode=subscribe, topic=https://www.youtube.com/feeds/videos.xml?channel_id=UCXuqSBlHAE6Xw-yeJA0Tunw
Updated lease expiry for channel UCXuqSBlHAE6Xw-yeJA0Tunw
```

This confirms YouTube's hub verified your callback URL.

### Step 4: Wait for Reconciliation

The reconciliation job runs every 30 minutes. Wait up to 30 minutes, then:

```bash
curl https://your-domain.com/api/users/test-user/feed
```

Expected response:
```json
{
  "videos": [
    {
      "videoId": "abc123",
      "channelId": "UCXuqSBlHAE6Xw-yeJA0Tunw",
      "publishedAt": "2025-12-05T10:00:00Z",
      "title": "Video Title",
      ...
    }
  ],
  "nextPageToken": null
}
```

### Step 5: Monitor Logs

**Docker**:
```bash
docker-compose logs -f
```

**Fly.io**:
```bash
flyctl logs -a metubeserver
```

**Railway**:
View logs in Railway Dashboard

**Cloudflare Tunnel**:
```bash
journalctl -u cloudflared -f
```

Look for:
- WebSub verification requests
- WebSub notifications
- Subscription maintenance job (hourly)
- Reconciliation job (every 30 minutes)
- Any errors or warnings

---

## Maintenance

### Updating the Server

#### Docker Compose

```bash
cd MeTubeServer
git pull
docker-compose down
docker-compose build
docker-compose up -d
```

#### Fly.io

```bash
cd MeTubeServer
git pull
flyctl deploy
```

#### Railway

Push to GitHub - Railway auto-deploys on push.

### Database Backup

#### Local/Docker

```bash
# Create backup
docker-compose exec metubeserver sqlite3 /app/data/metubeserver.db ".backup /app/data/backup.db"

# Copy backup to host
docker cp metubeserver_metubeserver_1:/app/data/backup.db ./backup-$(date +%Y%m%d).db

# Restore from backup
docker cp ./backup-20251206.db metubeserver_metubeserver_1:/app/data/metubeserver.db
docker-compose restart
```

#### Fly.io

```bash
# List volumes
flyctl volumes list

# SSH into instance
flyctl ssh console

# Inside container
sqlite3 /app/data/metubeserver.db ".backup /tmp/backup.db"
exit

# Download backup (requires flyctl proxy)
flyctl proxy 2222:22
scp -P 2222 root@localhost:/tmp/backup.db ./backup.db
```

### Monitoring

#### Set Up Health Check Monitoring

Use a service like:
- **UptimeRobot** (free): https://uptimerobot.com
- **Healthchecks.io** (free): https://healthchecks.io
- **Better Uptime** (free tier): https://betterstack.com

Configure:
- URL: `https://your-domain.com/health`
- Interval: 5 minutes
- Alert method: Email, SMS, Slack, etc.

#### Check YouTube Quota Usage

1. Go to [Google Cloud Console](https://console.cloud.google.com/apis/dashboard)
2. Select your MeTube project
3. Click **YouTube Data API v3**
4. View quota usage

Normal usage: 100-500 units/day for 100 channels.

#### Log Analysis

Look for warning patterns:
```bash
# Search for errors
docker-compose logs | grep -i error

# Check WebSub failures
docker-compose logs | grep "HMAC verification failed"

# Check API quota errors
docker-compose logs | grep "403"
```

### Scaling Considerations

#### When to Upgrade

- **More than 1000 users**: Consider PostgreSQL instead of SQLite
- **More than 10,000 channels**: Add caching layer (Redis)
- **High traffic**: Use load balancer with multiple instances

#### PostgreSQL Migration

1. Install PostgreSQL:
   ```bash
   docker run -d \
     --name postgres \
     -e POSTGRES_PASSWORD=secure_password \
     -e POSTGRES_DB=metubeserver \
     -p 5432:5432 \
     postgres:16
   ```

2. Update connection string:
   ```env
   ConnectionStrings__DefaultConnection=Host=localhost;Database=metubeserver;Username=postgres;Password=secure_password
   ```

3. Update `MeTubeServer.csproj`:
   ```xml
   <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.0" />
   ```

4. Update `Program.cs`:
   ```csharp
   builder.Services.AddDbContext<MeTubeDbContext>(options =>
       options.UseNpgsql(connectionString));
   ```

---

## Troubleshooting

### Server Won't Start

**Symptom**: Docker container exits immediately.

**Check**:
```bash
docker-compose logs
```

**Common causes**:
1. Invalid YouTube API key → Check `.env` file
2. Port 5000 already in use → Change port in `docker-compose.yml`
3. Database permissions → Check volume permissions

**Solutions**:
```bash
# Check port usage
lsof -i :5000

# Fix permissions
sudo chown -R $(id -u):$(id -g) ./data
```

### WebSub Verification Fails

**Symptom**: No "WebSub verification" logs, videos not appearing.

**Cause**: YouTube can't reach your callback URL.

**Debug steps**:

1. Test from external network:
   ```bash
   curl https://your-domain.com/health
   ```

2. Check Cloudflare Tunnel status:
   ```bash
   cloudflared tunnel info metubeserver
   ```

3. Verify DNS:
   ```bash
   nslookup metube.yourdomain.com
   ```

4. Check firewall (if not using tunnel):
   ```bash
   sudo ufw status
   ```

**Solution**: Ensure callback URL is:
- Publicly accessible
- Uses HTTPS (not HTTP)
- Returns 200 OK on GET requests

### Database Locked Errors

**Symptom**: Errors mentioning "database is locked".

**Cause**: SQLite doesn't handle concurrent writes well.

**Solutions**:

1. Reduce load (fewer users, channels)
2. Optimize queries (add indexes)
3. Migrate to PostgreSQL (see above)

**Temporary fix**:
```bash
# Restart server
docker-compose restart
```

### API Quota Exceeded

**Symptom**: 403 errors in logs, "quotaExceeded".

**Cause**: Exceeded YouTube's 10,000 units/day limit.

**Check usage**:
1. Google Cloud Console → APIs → YouTube Data API v3
2. View quota usage graph

**Solutions**:

1. **Request quota increase**:
   - Fill out [quota increase form](https://support.google.com/youtube/contact/yt_api_form)
   - Usually approved within 1-2 days

2. **Reduce API calls**:
   - Increase reconciliation interval (edit `ReconciliationJob.cs`)
   - Reduce channels being tracked
   - Rely more on WebSub (doesn't use quota)

3. **Wait for reset**: Quota resets daily at midnight Pacific Time

### Videos Not Appearing

**Symptom**: Feed returns empty array.

**Causes**:

1. **Channels inactive**: No videos published recently
   - Solution: Test with active channels (e.g., news channels)

2. **WebSub not working**: Verification failed
   - Solution: Check logs, verify callback URL

3. **Reconciliation pending**: Job hasn't run yet
   - Solution: Wait 30 minutes or restart server

4. **Database empty**: Channels not registered
   - Solution: Call register endpoint again

**Debug**:
```bash
# Check database
docker exec -it metubeserver_metubeserver_1 sqlite3 /app/data/metubeserver.db

sqlite> SELECT COUNT(*) FROM Channels;
sqlite> SELECT COUNT(*) FROM Videos;
sqlite> SELECT ChannelId, LeaseExpiresAt FROM Channels;
sqlite> .quit
```

### High Memory Usage

**Symptom**: Server uses excessive memory, OOM kills.

**Cause**: Large feed queries, memory leaks.

**Solutions**:

1. Reduce feed limit:
   ```bash
   # Call with smaller limit
   curl "https://your-domain.com/api/users/test/feed?limit=50"
   ```

2. Add memory limits:
   ```yaml
   # docker-compose.yml
   services:
     metubeserver:
       mem_limit: 512m
   ```

3. Restart periodically:
   ```bash
   # Add to crontab
   0 3 * * * docker-compose restart
   ```

### Cloudflare Tunnel Issues

**Symptom**: Tunnel connects but requests time out.

**Solutions**:

1. Check tunnel logs:
   ```bash
   journalctl -u cloudflared -f
   ```

2. Verify local server is running:
   ```bash
   curl http://localhost:5000/health
   ```

3. Check tunnel route:
   ```bash
   cloudflared tunnel route dns metubeserver metube.yourdomain.com
   ```

4. Restart tunnel:
   ```bash
   sudo systemctl restart cloudflared
   ```

---

## Security Best Practices

### 1. Protect API Key

```bash
# Never commit .env
echo ".env" >> .gitignore

# Use secrets management in production
flyctl secrets set YOUTUBE_API_KEY="..."
```

### 2. Enable HTTPS Only

Cloudflare Tunnel automatically provides HTTPS. For other deployments:

```bash
# Force HTTPS in Cloudflare
# Dashboard → SSL/TLS → Edge Certificates → Always Use HTTPS: On
```

### 3. Rate Limiting (Future)

Add rate limiting to prevent abuse:
```csharp
builder.Services.AddRateLimiter(options => { ... });
```

### 4. Update Regularly

```bash
# Subscribe to security updates
git watch github.com/pardeike/MeTubeServer

# Update dependencies
dotnet list package --outdated
dotnet add package [PackageName] --version [Version]
```

### 5. Monitor Logs

Set up alerts for:
- Failed authentication
- Repeated 4xx/5xx errors
- Database errors
- High API quota usage

---

## Cost Estimate

### Home Deployment

- **Electricity**: $2-5/month (low-power server)
- **Internet**: Included (assuming existing connection)
- **Domain**: $10-15/year (optional)
- **Cloudflare**: Free
- **Total**: ~$3/month

### Cloud Deployment (Fly.io)

- **Compute**: $0-5/month (depends on always-on requirement)
- **Storage**: $0.15/GB/month (1 GB = $0.15)
- **Bandwidth**: $0.02/GB (first 160 GB free)
- **Total**: ~$1-5/month

### Cloud Deployment (Railway)

- **Free tier**: $5 credit/month (sufficient for this app)
- **Paid**: $0.000463/GB-sec ($5-10/month for always-on)
- **Total**: Free (with $5 credit) or ~$5-10/month

---

## Summary

**Recommended Setup**:
- **Testing/Personal**: Home server + Cloudflare Tunnel
- **Small Scale**: Fly.io + Cloudflare proxy
- **Production**: Railway or Fly.io with PostgreSQL + monitoring

**Quick Start Commands**:
```bash
# Clone repo
git clone https://github.com/pardeike/MeTubeServer.git
cd MeTubeServer

# Configure
cp .env.example .env
nano .env  # Add your API key

# Deploy with Docker
docker-compose up -d

# OR Deploy to Fly.io
flyctl launch
flyctl secrets set YOUTUBE_API_KEY="..."
flyctl deploy

# Test
curl https://your-domain.com/health
```

For questions or issues, open an issue on GitHub or check the troubleshooting section above.

**Next Steps**: See [CLIENT.md](CLIENT.md) for integrating your app with the server.
