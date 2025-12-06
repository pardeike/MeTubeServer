using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MeTubeServer.BackgroundJobs;
using MeTubeServer.Data;
using MeTubeServer.Models;
using MeTubeServer.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection("YouTube"));
builder.Services.Configure<HubOptions>(builder.Configuration.GetSection("Hub"));

// Configure shutdown timeout (#36)
builder.Services.Configure<HostOptions>(options =>
{
    var hubOptions = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();
    options.ShutdownTimeout = TimeSpan.FromSeconds(hubOptions.ShutdownTimeoutSeconds);
});

// Configure request body size limits (#31)
builder.Services.Configure<KestrelServerOptions>(options =>
{
    var hubOptions = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();
    options.Limits.MaxRequestBodySize = hubOptions.MaxRequestBodySize;
});

// Add database context
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<MeTubeDbContext>(options =>
    options.UseSqlite(connectionString));

// Add HttpClient services with timeouts (#11)
var hubOptions = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();
builder.Services.AddHttpClient<YouTubeApiService>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(hubOptions.HttpClientTimeoutSeconds);
    });

builder.Services.AddHttpClient<WebSubService>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(hubOptions.HttpClientTimeoutSeconds);
    });

// Add custom services
builder.Services.AddScoped<AtomFeedParser>();
builder.Services.AddScoped<VideoEnrichmentService>();
builder.Services.AddSingleton<IBackgroundTaskQueue>(sp =>
{
    var options = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new BackgroundTaskQueue(options.BackgroundTaskQueueCapacity);
});

// Add background jobs
builder.Services.AddHostedService<QueuedHostedService>();
builder.Services.AddHostedService<SubscriptionMaintenanceJob>();
builder.Services.AddHostedService<ReconciliationJob>();

// Add rate limiting (#3)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    // Global rate limit per IP
    options.AddFixedWindowLimiter("fixed", limiterOptions =>
    {
        limiterOptions.PermitLimit = 100;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 10;
    });
    
    // WebSub endpoint rate limit (more restrictive)
    options.AddFixedWindowLimiter("websub", limiterOptions =>
    {
        limiterOptions.PermitLimit = 50;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 5;
    });
});

// Add OpenAPI/Swagger
builder.Services.AddOpenApi();

var app = builder.Build();

// Initialize database and validate configuration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MeTubeDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var youtubeOptions = scope.ServiceProvider.GetRequiredService<IOptions<YouTubeOptions>>().Value;
    var youtubeService = scope.ServiceProvider.GetRequiredService<YouTubeApiService>();
    
    // Create/migrate database (#38)
    try
    {
        await db.Database.EnsureCreatedAsync();
        logger.LogInformation("Database initialized successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize database");
        throw;
    }
    
    // Validate YouTube API key (#2)
    try
    {
        var isValid = await youtubeService.ValidateApiKeyAsync();
        if (!isValid)
        {
            logger.LogError("YouTube API key validation failed. Please check your configuration");
            throw new InvalidOperationException("Invalid YouTube API key");
        }
    }
    catch (Exception ex) when (ex is not InvalidOperationException)
    {
        logger.LogWarning(ex, "Could not validate YouTube API key at startup. Continuing anyway");
    }
    
    // Validate WebSub callback URL (#8)
    if (!youtubeOptions.CallbackBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning("CallbackBaseUrl should use HTTPS for WebSub to work properly: {CallbackBaseUrl}", 
            youtubeOptions.CallbackBaseUrl);
    }
    
    if (string.IsNullOrWhiteSpace(youtubeOptions.CallbackBaseUrl))
    {
        logger.LogError("CallbackBaseUrl is not configured. WebSub will not work");
    }
}

// Use rate limiting middleware
app.UseRateLimiter();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Health check endpoint
app.MapGet("/health", async (MeTubeDbContext db) =>
{
    try
    {
        // Check database connectivity
        var canConnect = await db.Database.CanConnectAsync();
        if (!canConnect)
        {
            return Results.Json(new { status = "unhealthy", message = "Database connection failed" }, statusCode: 503);
        }

        // Get some basic stats
        var channelCount = await db.Channels.CountAsync();
        var userCount = await db.Users.CountAsync();
        var videoCount = await db.Videos.CountAsync();

        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            stats = new
            {
                channels = channelCount,
                users = userCount,
                videos = videoCount
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", message = ex.Message }, statusCode: 503);
    }
})
.WithName("HealthCheck");

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
    IBackgroundTaskQueue taskQueue,
    ILogger<Program> logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    logger.LogDebug("Received WebSub notification, body length: {Length}", body.Length);

    // Parse the Atom feed once (#4 - Fix double parsing)
    var atomEntries = atomParser.ParseFeed(body);
    
    if (atomEntries.Count == 0)
    {
        logger.LogWarning("No entries found in Atom feed");
        return Results.Ok();
    }
    
    logger.LogDebug("Parsed {Count} entries from Atom feed", atomEntries.Count);

    // Verify HMAC if signature is present
    var signature = context.Request.Headers["X-Hub-Signature"].ToString();
    if (string.IsNullOrEmpty(signature))
    {
        signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
    }

    if (!string.IsNullOrEmpty(signature) && atomEntries.Count > 0)
    {
        // Get the channel from the first entry
        var channelIdFromFeed = atomEntries[0].ChannelId;
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChannelId == channelIdFromFeed);

        if (channel != null && !string.IsNullOrEmpty(channel.HubSecret))
        {
            if (!webSubService.VerifySignature(body, signature, channel.HubSecret))
            {
                logger.LogWarning("HMAC verification failed for channel {ChannelId}", channelIdFromFeed);
                return Results.Unauthorized();
            }
            logger.LogDebug("HMAC verified successfully for channel {ChannelId}", channelIdFromFeed);
        }
    }

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
            logger.LogDebug("Video {VideoId} already exists, skipping", entry.VideoId);
            continue;
        }

        // Check if this is older than what we've already seen
        if (channel.LastSeenPublishedAt.HasValue && entry.PublishedAt <= channel.LastSeenPublishedAt.Value)
        {
            logger.LogDebug("Video {VideoId} is older than last seen, skipping", entry.VideoId);
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
        
        // Queue video enrichment (#6)
        var videoId = entry.VideoId;
        await taskQueue.QueueBackgroundWorkItemAsync(async (sp, ct) =>
        {
            var enrichmentService = sp.GetRequiredService<VideoEnrichmentService>();
            var dbContext = sp.GetRequiredService<MeTubeDbContext>();
            await enrichmentService.EnrichVideoAsync(videoId, dbContext, ct);
        });
    }

    await db.SaveChangesAsync();

    return Results.Ok();
})
.WithName("WebSubNotification")
.RequireRateLimiting("websub");

// App Integration Endpoints
app.MapPost("/api/users/{appUserId}/channels", async (
    string appUserId,
    RegisterChannelsRequest request,
    MeTubeDbContext db,
    WebSubService webSubService,
    YouTubeApiService youtubeApi,
    IBackgroundTaskQueue taskQueue,
    ILogger<Program> logger) =>
{
    // Deduplicate channel IDs (#20)
    var uniqueChannelIds = request.ChannelIds.Distinct().ToList();
    
    logger.LogInformation("Registering {Count} unique channels for user {UserId}",
        uniqueChannelIds.Count, appUserId);

    // Use transaction for data consistency (#5)
    using var transaction = await db.Database.BeginTransactionAsync();
    try
    {
        // Upsert user
        var user = await db.Users.FirstOrDefaultAsync(u => u.AppUserId == appUserId);
        if (user == null)
        {
            user = new User { AppUserId = appUserId };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        // Batch fetch uploads playlist IDs for new channels (#40)
        var existingChannelIds = await db.Channels
            .Where(c => uniqueChannelIds.Contains(c.ChannelId))
            .Select(c => c.ChannelId)
            .ToListAsync();
        
        var newChannelIds = uniqueChannelIds.Except(existingChannelIds).ToList();
        
        if (newChannelIds.Count > 0)
        {
            logger.LogInformation("Creating {Count} new channels", newChannelIds.Count);
            
            // Create new channels first
            var newChannels = new List<Channel>();
            foreach (var channelId in newChannelIds)
            {
                var topicUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";
                var hubSecret = webSubService.GenerateHubSecret();

                var channel = new Channel
                {
                    ChannelId = channelId,
                    TopicUrl = topicUrl,
                    HubSecret = hubSecret
                };

                db.Channels.Add(channel);
                newChannels.Add(channel);
            }
            
            await db.SaveChangesAsync();
            
            // Queue background tasks for WebSub and metadata (#1)
            foreach (var channel in newChannels)
            {
                var channelId = channel.ChannelId;
                var topicUrl = channel.TopicUrl;
                var hubSecret = channel.HubSecret ?? string.Empty;
                
                // Queue WebSub subscription
                await taskQueue.QueueBackgroundWorkItemAsync(async (sp, ct) =>
                {
                    var webSub = sp.GetRequiredService<WebSubService>();
                    var log = sp.GetRequiredService<ILogger<Program>>();
                    try
                    {
                        await webSub.SubscribeAsync(topicUrl, hubSecret, ct);
                        log.LogInformation("Subscribed to WebSub for channel {ChannelId}", channelId);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Error subscribing to WebSub for channel {ChannelId}", channelId);
                    }
                });
                
                // Queue uploads playlist and metadata fetch
                await taskQueue.QueueBackgroundWorkItemAsync(async (sp, ct) =>
                {
                    var ytApi = sp.GetRequiredService<YouTubeApiService>();
                    var dbContext = sp.GetRequiredService<MeTubeDbContext>();
                    var log = sp.GetRequiredService<ILogger<Program>>();
                    try
                    {
                        var uploadsPlaylistId = await ytApi.GetUploadsPlaylistIdAsync(channelId, ct);
                        var metadata = await ytApi.GetChannelMetadataAsync(channelId, ct);
                        
                        var ch = await dbContext.Channels.FirstOrDefaultAsync(c => c.ChannelId == channelId, ct);
                        if (ch != null)
                        {
                            ch.UploadsPlaylistId = uploadsPlaylistId;
                            if (metadata != null)
                            {
                                ch.ChannelName = metadata.Title;
                                ch.ChannelThumbnailUrl = metadata.ThumbnailUrl;
                                ch.MetadataLastUpdated = DateTimeOffset.UtcNow;
                            }
                            await dbContext.SaveChangesAsync(ct);
                            log.LogInformation("Updated metadata for channel {ChannelId}", channelId);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Error fetching metadata for channel {ChannelId}", channelId);
                    }
                });
            }
        }

        // Link user to all channels
        var allChannels = await db.Channels
            .Where(c => uniqueChannelIds.Contains(c.ChannelId))
            .ToListAsync();
        
        foreach (var channel in allChannels)
        {
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
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("Successfully registered channels for user {UserId}", appUserId);
        return Results.Ok(new { message = "Channels registered successfully" });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        logger.LogError(ex, "Error registering channels for user {UserId}", appUserId);
        return Results.Problem("Failed to register channels");
    }
})
.WithName("RegisterChannels")
.RequireRateLimiting("fixed");

app.MapGet("/api/users/{appUserId}/feed", async (
    string appUserId,
    MeTubeDbContext db,
    IOptions<HubOptions> options,
    string? since = null,
    int? limit = null) =>
{
    var hubOptions = options.Value;
    
    // Apply default and max limits (#24)
    var effectiveLimit = limit ?? hubOptions.DefaultFeedLimit;
    if (effectiveLimit > hubOptions.MaxFeedLimit)
        effectiveLimit = hubOptions.MaxFeedLimit;

    var user = await db.Users
        .Include(u => u.UserChannels)
        .FirstOrDefaultAsync(u => u.AppUserId == appUserId);

    if (user == null)
    {
        return Results.NotFound(new { message = "User not found" });
    }

    var channelIds = user.UserChannels.Select(uc => uc.ChannelId).ToList();
    
    if (channelIds.Count == 0)
    {
        return Results.Ok(new FeedResponse { Videos = new List<VideoDto>(), NextCursor = null });
    }

    DateTimeOffset? sinceDate = null;
    if (!string.IsNullOrEmpty(since) && DateTimeOffset.TryParse(since, out var parsedDate))
    {
        sinceDate = parsedDate;
    }

    // Fix N+1 pattern by projecting directly to DTO (#33)
    var query = from v in db.Videos
                join c in db.Channels on v.ChannelId equals c.Id
                where channelIds.Contains(v.ChannelId)
                select new { Video = v, Channel = c };

    if (sinceDate.HasValue)
    {
        query = query.Where(x => x.Video.PublishedAt > sinceDate.Value);
    }

    var results = await query
        .OrderByDescending(x => x.Video.PublishedAt)
        .Take(effectiveLimit + 1) // Fetch one extra to determine if there are more results
        .Select(x => new VideoDto
        {
            VideoId = x.Video.VideoId,
            ChannelId = x.Channel.ChannelId,
            PublishedAt = x.Video.PublishedAt,
            Title = x.Video.Title,
            Description = x.Video.Description,
            ThumbnailUrl = x.Video.ThumbnailUrl,
            Duration = x.Video.Duration
        })
        .ToListAsync();

    // Implement pagination (#13)
    string? nextCursor = null;
    if (results.Count > effectiveLimit)
    {
        // There are more results - use the last item that will be returned for the cursor
        nextCursor = results[effectiveLimit - 1].PublishedAt.ToString("O");
        results = results.Take(effectiveLimit).ToList();
    }

    var response = new FeedResponse
    {
        Videos = results,
        NextCursor = nextCursor
    };

    return Results.Ok(response);
})
.WithName("GetUserFeed")
.RequireRateLimiting("fixed");

app.Run();
