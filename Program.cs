using Microsoft.EntityFrameworkCore;
using MeTubeServer.BackgroundJobs;
using MeTubeServer.Data;
using MeTubeServer.Models;
using MeTubeServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection("YouTube"));
builder.Services.Configure<HubOptions>(builder.Configuration.GetSection("Hub"));

// Add database context
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<MeTubeDbContext>(options =>
    options.UseSqlite(connectionString));

// Add HttpClient services
builder.Services.AddHttpClient<YouTubeApiService>();
builder.Services.AddHttpClient<WebSubService>();

// Add custom services
builder.Services.AddScoped<AtomFeedParser>();

// Add background jobs
builder.Services.AddHostedService<SubscriptionMaintenanceJob>();
builder.Services.AddHostedService<ReconciliationJob>();

// Add OpenAPI/Swagger
builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// WebSub Endpoints
app.MapGet("/websub/youtube", async (HttpContext context, MeTubeDbContext db, ILogger<Program> logger) =>
{
    var mode = context.Request.Query["hub.mode"].ToString();
    var topic = context.Request.Query["hub.topic"].ToString();
    var challenge = context.Request.Query["hub.challenge"].ToString();
    var leaseSecondsStr = context.Request.Query["hub.lease_seconds"].ToString();

    logger.LogInformation("WebSub verification: mode={Mode}, topic={Topic}", mode, topic);

    var channel = await db.Channels.FirstOrDefaultAsync(c => c.TopicUrl == topic);

    if (channel == null)
    {
        logger.LogWarning("Channel not found for topic: {Topic}", topic);
        return Results.NotFound();
    }

    if (mode == "subscribe")
    {
        if (int.TryParse(leaseSecondsStr, out var leaseSeconds))
        {
            channel.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
            await db.SaveChangesAsync();
            logger.LogInformation("Updated lease expiry for channel {ChannelId}: {LeaseExpiresAt}",
                channel.ChannelId, channel.LeaseExpiresAt);
        }
    }
    else if (mode == "unsubscribe")
    {
        channel.LeaseExpiresAt = null;
        await db.SaveChangesAsync();
        logger.LogInformation("Cleared lease expiry for channel {ChannelId}", channel.ChannelId);
    }

    return Results.Text(challenge, "text/plain");
})
.WithName("WebSubVerification");

app.MapPost("/websub/youtube", async (
    HttpContext context,
    MeTubeDbContext db,
    AtomFeedParser atomParser,
    WebSubService webSubService,
    ILogger<Program> logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    logger.LogInformation("Received WebSub notification, body length: {Length}", body.Length);

    // Verify HMAC if signature is present
    var signature = context.Request.Headers["X-Hub-Signature"].ToString();
    if (string.IsNullOrEmpty(signature))
    {
        signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
    }

    if (!string.IsNullOrEmpty(signature))
    {
        // We need to find the channel to get the secret
        // Parse the feed first to get channelId
        var entries = atomParser.ParseFeed(body);
        if (entries.Count > 0)
        {
            var channelIdFromFeed = entries[0].ChannelId;
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChannelId == channelIdFromFeed);

            if (channel != null && !string.IsNullOrEmpty(channel.HubSecret))
            {
                if (!webSubService.VerifySignature(body, signature, channel.HubSecret))
                {
                    logger.LogWarning("HMAC verification failed for channel {ChannelId}", channelIdFromFeed);
                    return Results.Unauthorized();
                }
                logger.LogInformation("HMAC verified successfully for channel {ChannelId}", channelIdFromFeed);
            }
        }
    }

    // Parse the Atom feed
    var atomEntries = atomParser.ParseFeed(body);
    logger.LogInformation("Parsed {Count} entries from Atom feed", atomEntries.Count);

    foreach (var entry in atomEntries)
    {
        var channel = await db.Channels
            .FirstOrDefaultAsync(c => c.ChannelId == entry.ChannelId);

        if (channel == null)
        {
            logger.LogWarning("Channel {ChannelId} not found in database", entry.ChannelId);
            continue;
        }

        // Check if video already exists
        var existingVideo = await db.Videos
            .FirstOrDefaultAsync(v => v.VideoId == entry.VideoId);

        if (existingVideo != null)
        {
            logger.LogInformation("Video {VideoId} already exists, skipping", entry.VideoId);
            continue;
        }

        // Check if this is older than what we've already seen
        if (channel.LastSeenPublishedAt.HasValue && entry.PublishedAt <= channel.LastSeenPublishedAt.Value)
        {
            logger.LogInformation("Video {VideoId} is older than last seen, skipping", entry.VideoId);
            continue;
        }

        // Add new video
        var video = new Video
        {
            VideoId = entry.VideoId,
            ChannelId = channel.Id,
            PublishedAt = entry.PublishedAt,
            Title = entry.Title
        };

        db.Videos.Add(video);

        // Update channel's last seen timestamp
        if (entry.PublishedAt > (channel.LastSeenPublishedAt ?? DateTimeOffset.MinValue))
        {
            channel.LastSeenPublishedAt = entry.PublishedAt;
        }

        logger.LogInformation("Added new video {VideoId} for channel {ChannelId}", entry.VideoId, entry.ChannelId);
    }

    await db.SaveChangesAsync();

    return Results.Ok();
})
.WithName("WebSubNotification");

// App Integration Endpoints
app.MapPost("/api/users/{appUserId}/channels", async (
    string appUserId,
    RegisterChannelsRequest request,
    MeTubeDbContext db,
    WebSubService webSubService,
    YouTubeApiService youtubeApi,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Registering channels for user {UserId}: {Count} channels",
        appUserId, request.ChannelIds.Count);

    // Upsert user
    var user = await db.Users.FirstOrDefaultAsync(u => u.AppUserId == appUserId);
    if (user == null)
    {
        user = new User { AppUserId = appUserId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    foreach (var channelId in request.ChannelIds)
    {
        // Check if channel exists
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChannelId == channelId);

        if (channel == null)
        {
            // Create new channel
            var topicUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";
            var hubSecret = webSubService.GenerateHubSecret();

            channel = new Channel
            {
                ChannelId = channelId,
                TopicUrl = topicUrl,
                HubSecret = hubSecret
            };

            db.Channels.Add(channel);
            await db.SaveChangesAsync();

            logger.LogInformation("Created new channel {ChannelId}", channelId);

            // Subscribe to WebSub (fire and forget with error logging)
            _ = Task.Run(async () =>
            {
                try
                {
                    await webSubService.SubscribeAsync(topicUrl, hubSecret);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error subscribing to WebSub for channel {ChannelId}", channelId);
                }
            });

            // Fetch uploads playlist ID (fire and forget with error logging)
            _ = Task.Run(async () =>
            {
                try
                {
                    var uploadsPlaylistId = await youtubeApi.GetUploadsPlaylistIdAsync(channelId);
                    if (!string.IsNullOrEmpty(uploadsPlaylistId))
                    {
                        using var scope = app.Services.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
                        var ch = await dbContext.Channels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
                        if (ch != null)
                        {
                            ch.UploadsPlaylistId = uploadsPlaylistId;
                            await dbContext.SaveChangesAsync();
                            logger.LogInformation("Updated uploads playlist ID for channel {ChannelId}", channelId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching uploads playlist ID for channel {ChannelId}", channelId);
                }
            });
        }

        // Upsert UserChannel
        var userChannel = await db.UserChannels
            .FirstOrDefaultAsync(uc => uc.UserId == user.Id && uc.ChannelId == channel.Id);

        if (userChannel == null)
        {
            userChannel = new UserChannel
            {
                UserId = user.Id,
                ChannelId = channel.Id
            };
            db.UserChannels.Add(userChannel);
            logger.LogInformation("Linked user {UserId} to channel {ChannelId}", appUserId, channelId);
        }
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Channels registered successfully" });
})
.WithName("RegisterChannels");

app.MapGet("/api/users/{appUserId}/feed", async (
    string appUserId,
    MeTubeDbContext db,
    string? since = null,
    int limit = 50) =>
{
    var user = await db.Users
        .Include(u => u.UserChannels)
        .ThenInclude(uc => uc.Channel)
        .FirstOrDefaultAsync(u => u.AppUserId == appUserId);

    if (user == null)
    {
        return Results.NotFound(new { message = "User not found" });
    }

    var channelIds = user.UserChannels.Select(uc => uc.ChannelId).ToList();

    DateTimeOffset? sinceDate = null;
    if (!string.IsNullOrEmpty(since) && DateTimeOffset.TryParse(since, out var parsedDate))
    {
        sinceDate = parsedDate;
    }

    var query = db.Videos
        .Include(v => v.Channel)
        .Where(v => channelIds.Contains(v.ChannelId));

    if (sinceDate.HasValue)
    {
        query = query.Where(v => v.PublishedAt > sinceDate.Value);
    }

    var videos = await query
        .OrderByDescending(v => v.PublishedAt)
        .Take(limit)
        .ToListAsync();

    var response = new FeedResponse
    {
        Videos = videos.Select(v => new VideoDto
        {
            VideoId = v.VideoId,
            ChannelId = v.Channel.ChannelId,
            PublishedAt = v.PublishedAt,
            Title = v.Title,
            Description = v.Description,
            ThumbnailUrl = v.ThumbnailUrl,
            Duration = v.Duration
        }).ToList()
    };

    return Results.Ok(response);
})
.WithName("GetUserFeed");

app.Run();
