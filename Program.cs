using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MeTubeServer.BackgroundJobs;
using MeTubeServer.Data;
using MeTubeServer.Models;
using MeTubeServer.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection("YouTube"));
builder.Services.Configure<HubOptions>(builder.Configuration.GetSection("Hub"));
builder.Services.Configure<ReconciliationSettings>(builder.Configuration.GetSection("Reconciliation"));

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

// Add HttpClient services with timeouts (#11) and circuit breaker (#15)
var hubOptions = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();

// Circuit breaker policy: opens after 5 consecutive failures, stays open for 30 seconds
var circuitBreakerPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));

// Retry policy: retry 3 times with exponential backoff
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

builder.Services.AddHttpClient<YouTubeApiService>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(hubOptions.HttpClientTimeoutSeconds);
    })
    .AddPolicyHandler(retryPolicy)
    .AddPolicyHandler(circuitBreakerPolicy);

builder.Services.AddHttpClient<WebSubService>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(hubOptions.HttpClientTimeoutSeconds);
    })
    .AddPolicyHandler(retryPolicy)
    .AddPolicyHandler(circuitBreakerPolicy);

// Add custom services
builder.Services.AddScoped<AtomFeedParser>();
builder.Services.AddScoped<VideoEnrichmentService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddSingleton<YouTubeQuotaTracker>();
builder.Services.AddSingleton<YouTubeApiStatusService>();
builder.Services.AddSingleton<BackgroundJobHealthCheck>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<IBackgroundTaskQueue>(sp =>
{
    var options = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new BackgroundTaskQueue(options.BackgroundTaskQueueCapacity);
});

// Add background jobs (ReconciliationJob removed - now on-demand via API)
builder.Services.AddHostedService<QueuedHostedService>();
builder.Services.AddHostedService<SubscriptionMaintenanceJob>();
builder.Services.AddHostedService<ChannelCleanupJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YouTubeApiStatusService>());

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

// Add output caching (#34)
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(builder => builder
        .Expire(TimeSpan.FromMinutes(5))
        .Tag("feed"));
});

// Add CORS if configured (#32)
var hasCorsOrigins = !string.IsNullOrWhiteSpace(hubOptions.CorsAllowedOrigins);
if (hasCorsOrigins)
{
    var origins = hubOptions.CorsAllowedOrigins
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(origins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        });
    });
}

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
    var apiStatusService = scope.ServiceProvider.GetRequiredService<YouTubeApiStatusService>();
    
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
    // Note: Server will start even if quota is exceeded. The YouTubeApiStatusService
    // will track the quota status and periodically check for recovery.
    try
    {
        var isValid = await youtubeService.ValidateApiKeyAsync(default, skipStatusCheck: true);
        if (!isValid)
        {
            // Check if this is a quota issue - if so, just warn and continue
            if (!apiStatusService.IsApiAvailable)
            {
                logger.LogWarning("YouTube API quota exceeded at startup. Server will start, but API calls will be suspended until quota is released. " +
                    "The service will automatically resume API calls when quota becomes available (resets at midnight Pacific Time).");
            }
            else
            {
                // Validation failed for a non-quota reason (invalid API key)
                logger.LogError("YouTube API key validation failed. Please check your configuration");
                throw new InvalidOperationException("Invalid YouTube API key");
            }
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

// Use CORS if configured (#32)
if (hasCorsOrigins)
{
    app.UseCors();
}

// Use output caching (#34)
app.UseOutputCache();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Health check endpoints (#21)
app.MapGet("/health", async (MeTubeDbContext db, YouTubeQuotaTracker quotaTracker, YouTubeApiStatusService apiStatus) =>
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
        
        // Get quota stats
        var quotaStats = quotaTracker.GetQuotaStats();

        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            stats = new
            {
                channels = channelCount,
                users = userCount,
                videos = videoCount
            },
            quota = new
            {
                used = quotaStats.Used,
                remaining = quotaStats.Remaining,
                limit = quotaStats.Limit,
                percentUsed = Math.Round(quotaStats.PercentUsed, 1)
            },
            youtubeApi = new
            {
                available = apiStatus.IsApiAvailable,
                quotaExceededAt = apiStatus.GetQuotaExceededAt()
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", message = ex.Message }, statusCode: 503);
    }
})
.WithName("HealthCheck");

// Detailed health check with stats
app.MapGet("/health/details", async (MeTubeDbContext db, YouTubeQuotaTracker quotaTracker, YouTubeApiStatusService apiStatus) =>
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
        
        // Get quota stats
        var quotaStats = quotaTracker.GetQuotaStats();

        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            stats = new
            {
                channels = channelCount,
                users = userCount,
                videos = videoCount
            },
            quota = new
            {
                used = quotaStats.Used,
                remaining = quotaStats.Remaining,
                limit = quotaStats.Limit,
                percentUsed = Math.Round(quotaStats.PercentUsed, 1),
                date = quotaStats.Date.ToString("yyyy-MM-dd")
            },
            youtubeApi = new
            {
                available = apiStatus.IsApiAvailable,
                quotaExceededAt = apiStatus.GetQuotaExceededAt()
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", message = ex.Message }, statusCode: 503);
    }
})
.WithName("HealthCheckDetails");

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
    MetricsService metrics,
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
        
        // Record metric (#18)
        metrics.RecordWebSubNotification(channelIdFromFeed);

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
        
        // Record metric (#18)
        metrics.RecordVideoAdded(entry.ChannelId, entry.VideoId);
        
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
    MetricsService metrics,
    ILogger<Program> logger) =>
{
    // Deduplicate channel IDs (#20)
    var uniqueChannelIds = request.ChannelIds.Distinct().ToList();
    
    logger.LogInformation("Registering {Count} unique channels for user {UserId}",
        uniqueChannelIds.Count, appUserId);

    // Capture channel info for background processing
    List<(string ChannelId, string TopicUrl, string HubSecret)>? newChannelInfo = null;

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
            newChannelInfo = new List<(string, string, string)>();
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
                newChannelInfo.Add((channelId, topicUrl, hubSecret));
            }
            
            await db.SaveChangesAsync();
        }

        // Link user to all channels
        var allChannels = await db.Channels
            .Where(c => uniqueChannelIds.Contains(c.ChannelId))
            .ToListAsync();
        
        // Fetch all existing UserChannel relationships in a single query to avoid N+1
        var channelIds = allChannels.Select(c => c.Id).ToHashSet();
        var existingUserChannels = (await db.UserChannels
            .Where(uc => uc.UserId == user.Id && channelIds.Contains(uc.ChannelId))
            .Select(uc => uc.ChannelId)
            .ToListAsync()).ToHashSet();
        
        foreach (var channel in allChannels)
        {
            if (!existingUserChannels.Contains(channel.Id))
            {
                var userChannel = new UserChannel
                {
                    UserId = user.Id,
                    ChannelId = channel.Id
                };
                db.UserChannels.Add(userChannel);
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        // Record metric (#18)
        metrics.RecordChannelsRegistered(uniqueChannelIds.Count, appUserId);
        
        logger.LogInformation("Successfully registered channels for user {UserId}", appUserId);
        
        // Queue background tasks asynchronously after response is returned
        // This prevents blocking the HTTP response when the queue is full
        // Note: The Task.Run may still block if the queue fills up again, but this
        // happens in the background after the client has received the response
        if (newChannelInfo != null && newChannelInfo.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var (channelId, topicUrl, hubSecret) in newChannelInfo)
                    {
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
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error queuing background tasks for user {UserId}", appUserId);
                }
            });
        }
        
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

// On-demand reconciliation endpoint (#quota-optimization)
app.MapPost("/api/users/{appUserId}/reconcile", async (
    string appUserId,
    MeTubeDbContext db,
    ReconciliationService reconciliationService,
    ILogger<Program> logger) =>
{
    try
    {
        // Validate user exists
        var user = await db.Users.FirstOrDefaultAsync(u => u.AppUserId == appUserId);
        if (user == null)
        {
            logger.LogWarning("Reconcile requested for unknown user {UserId}", appUserId);
            return Results.NotFound(new { message = "User not found" });
        }

        // Perform reconciliation
        var newVideosCount = await reconciliationService.ReconcileUserChannelsAsync(
            user.Id, 
            db);

        logger.LogInformation("Reconciled {Count} new videos for user {UserId}", 
            newVideosCount, appUserId);

        return Results.Ok(new 
        { 
            message = "Reconciliation completed", 
            newVideosCount = newVideosCount 
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error reconciling channels for user {UserId}", appUserId);
        return Results.Problem("Failed to reconcile channels");
    }
})
.WithName("ReconcileUserChannels")
.RequireRateLimiting("fixed");

app.MapGet("/api/users/{appUserId}/feed", async (
    string appUserId,
    MeTubeDbContext db,
    IOptions<HubOptions> options,
    MetricsService metrics,
    string? since = null,
    int? limit = null) =>
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var hubOptions = options.Value;
    
    // Apply default and max limits (#24)
    var effectiveLimit = limit ?? hubOptions.DefaultFeedLimit;
    if (effectiveLimit > hubOptions.MaxFeedLimit)
        effectiveLimit = hubOptions.MaxFeedLimit;

    // Use compiled query for better performance (#35)
    var user = await MeTubeDbContext.GetUserByAppUserIdAsync(db, appUserId);

    if (user == null)
    {
        return Results.NotFound(new { message = "User not found" });
    }

    var channelIds = user.UserChannels.Select(uc => uc.ChannelId).ToList();
    
    if (channelIds.Count == 0)
    {
        return Results.Ok(new FeedResponse { Videos = Array.Empty<VideoDto>(), NextCursor = null });
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

    // SQLite has limitations with DateTimeOffset comparisons in WHERE clauses when combined with joins,
    // so we fetch and filter client-side. This is acceptable because the query is pre-filtered by 
    // user's channel subscriptions, typically resulting in a manageable dataset for most users.
    var allResults = await query.ToListAsync();
    
    // Apply date filter client-side if provided
    var filteredResults = sinceDate.HasValue
        ? allResults.Where(x => x.Video.PublishedAt > sinceDate.Value)
        : allResults;
    
    var results = filteredResults
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
        .ToList();

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

    // Record metrics (#18)
    stopwatch.Stop();
    metrics.RecordFeedRequest(appUserId);
    metrics.RecordFeedRequestDuration(stopwatch.Elapsed.TotalMilliseconds, appUserId);
    
    return Results.Ok(response);
})
.WithName("GetUserFeed")
.RequireRateLimiting("fixed")
.CacheOutput(policy => policy
    .Expire(TimeSpan.FromMinutes(2))
    .SetVaryByRouteValue("appUserId")
    .SetVaryByQuery("since", "limit")
    .Tag("feed"));

app.Run();
