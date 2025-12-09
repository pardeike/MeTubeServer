# MeTube Hub Server - Client Integration Guide

## Purpose

This document provides comprehensive guidance for integrating the MeTube iOS/tvOS app with the MeTube Hub Server. It's designed to help an AI agent (or developer) refactor an existing YouTube-based app to use this server efficiently.

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Migration Strategy](#migration-strategy)
3. [API Reference](#api-reference)
4. [Implementation Guide](#implementation-guide)
5. [Best Practices](#best-practices)
6. [Error Handling](#error-handling)
7. [Testing](#testing)

---

## Architecture Overview

### Current vs New Architecture

**Current (Direct YouTube API)**:
```
MeTube App → YouTube Data API → Display Videos
     ↓
User OAuth → subscriptions.list → Channel IDs
     ↓
For each channel:
  - playlistItems.list (uploads playlist)
  - videos.list (metadata)
```

**Quota Impact**: High - every app instance polls YouTube independently.

**New (Hub Server with On-Demand Reconciliation)**:
```
MeTube App → Hub Server → Display Videos
     ↓              ↓
User OAuth    YouTube WebSub + Data API
     ↓              ↓
Send channel IDs → Shared cache
     ↓              ↓
Pull-to-refresh → On-demand reconciliation (backup)
                    ↓
                One subscription per channel
```

**Quota Impact**: Minimal - server caches everything, WebSub provides real-time updates, reconciliation only when users actively refresh.

### Responsibilities

**App Responsibilities**:
- User authentication (Google OAuth)
- Get user's subscribed channel IDs from YouTube
- Send channel IDs to hub server
- **Call reconciliation endpoint on pull-to-refresh and foreground transition**
- Fetch and display aggregated feed from hub
- Local caching and UI

**Hub Server Responsibilities**:
- Subscribe to WebSub for channels
- Receive push notifications (real-time, no quota)
- **Reconcile on-demand when app requests it**
- Cache video metadata
- Serve aggregated feed

### Key Benefit

**Before**: 100 users with 50 channels each = 5,000 individual YouTube API calls per refresh
**After**: 100 users with 50 channels (assuming 80% overlap) = ~1,000 unique channels, managed by server

---

## Migration Strategy

### Phase 1: Parallel Operation (Recommended)

Run both systems side-by-side to validate:

```swift
class VideoFeedManager {
    let useHubServer = UserDefaults.standard.bool(forKey: "useHubServer")
    
    func fetchVideos() async throws -> [Video] {
        if useHubServer {
            return try await fetchFromHub()
        } else {
            return try await fetchFromYouTubeDirect()
        }
    }
}
```

**Timeline**: 2-4 weeks with beta testers.

### Phase 2: Full Migration

Once validated, remove direct YouTube code and rely solely on hub.

### Phase 3: Optimization

Add features that leverage the hub:
- Real-time notifications via APNs (future)
- Sync watched status across devices
- Better offline support

---

## API Reference

### Base Configuration

```swift
struct HubConfig {
    static let baseURL = "https://your-hub-server.com"
    
    // For development
    #if DEBUG
    static let baseURL = "http://localhost:5000"
    #endif
}
```

### Authentication

**Important**: The hub server does NOT require authentication (in current implementation). However, you should consider adding it for production to prevent abuse.

For now, use a stable user ID that persists across app installs:

```swift
class UserManager {
    // Use iCloud key-value store or keychain
    var userId: String {
        if let saved = UserDefaults.standard.string(forKey: "hubUserId") {
            return saved
        }
        let newId = UUID().uuidString
        UserDefaults.standard.set(newId, forKey: "hubUserId")
        return newId
    }
}
```

### Endpoint 1: Health Check

**Purpose**: Verify hub server is accessible.

```
GET /health
```

**Response**:
```json
{
  "status": "healthy",
  "timestamp": "2025-12-06T12:00:00Z",
  "stats": {
    "channels": 150,
    "users": 45,
    "videos": 3200
  }
}
```

**Swift Implementation**:
```swift
struct HealthResponse: Codable {
    let status: String
    let timestamp: Date
    let stats: Stats
    
    struct Stats: Codable {
        let channels: Int
        let users: Int
        let videos: Int
    }
}

func checkHealth() async throws -> HealthResponse {
    let url = URL(string: "\(HubConfig.baseURL)/health")!
    let (data, response) = try await URLSession.shared.data(from: url)
    
    guard let httpResponse = response as? HTTPURLResponse,
          httpResponse.statusCode == 200 else {
        throw HubError.serverUnhealthy
    }
    
    let decoder = JSONDecoder()
    decoder.dateDecodingStrategy = .iso8601
    return try decoder.decode(HealthResponse.self, from: data)
}
```

**When to Call**: On app launch, before making other API calls.

---

### Endpoint 2: Register Channels

**Purpose**: Tell the hub which channels this user follows.

```
POST /api/users/{userId}/channels
Content-Type: application/json

{
  "channelIds": ["UC123...", "UC456...", ...]
}
```

**Response**:
```json
{
  "message": "Channels registered successfully"
}
```

**Swift Implementation**:
```swift
struct RegisterChannelsRequest: Codable {
    let channelIds: [String]
}

struct RegisterChannelsResponse: Codable {
    let message: String
}

func registerChannels(userId: String, channelIds: [String]) async throws {
    let url = URL(string: "\(HubConfig.baseURL)/api/users/\(userId)/channels")!
    var request = URLRequest(url: url)
    request.httpMethod = "POST"
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    
    let body = RegisterChannelsRequest(channelIds: channelIds)
    request.httpBody = try JSONEncoder().encode(body)
    
    let (data, response) = try await URLSession.shared.data(for: request)
    
    guard let httpResponse = response as? HTTPURLResponse,
          httpResponse.statusCode == 200 else {
        throw HubError.registrationFailed
    }
    
    let result = try JSONDecoder().decode(RegisterChannelsResponse.self, from: data)
    print("Registration: \(result.message)")
}
```

**When to Call**:
- On app first launch (after OAuth)
- When user's YouTube subscriptions change
- Periodically (e.g., daily) to keep in sync

**Important Considerations**:
- Send ALL channel IDs the user follows, not incremental updates
- Limit: reasonable (tested up to 1000+, but be mindful)
- The hub will automatically subscribe to new channels
- Existing channels are updated (idempotent)

**Detecting Subscription Changes**:
```swift
func syncSubscriptions() async throws {
    // 1. Get latest from YouTube
    let youtubeChannels = try await fetchYouTubeSubscriptions()
    let channelIds = youtubeChannels.map { $0.id }
    
    // 2. Get cached from last sync
    let cachedIds = UserDefaults.standard.stringArray(forKey: "lastSyncedChannels") ?? []
    
    // 3. If changed, update hub
    if Set(channelIds) != Set(cachedIds) {
        try await registerChannels(userId: userManager.userId, channelIds: channelIds)
        UserDefaults.standard.set(channelIds, forKey: "lastSyncedChannels")
    }
}
```

---

### Endpoint 3: Reconcile Channels (On-Demand)

**Purpose**: Trigger reconciliation to check for videos that may have been missed by WebSub. Call this on pull-to-refresh or when app comes to foreground.

```
POST /api/users/{userId}/reconcile
```

**Response**:
```json
{
  "message": "Reconciliation completed",
  "newVideosCount": 5
}
```

**Swift Implementation**:
```swift
struct ReconcileResponse: Codable {
    let message: String
    let newVideosCount: Int
}

func reconcileChannels(userId: String) async throws -> Int {
    let url = URL(string: "\(HubConfig.baseURL)/api/users/\(userId)/reconcile")!
    var request = URLRequest(url: url)
    request.httpMethod = "POST"
    
    let (data, response) = try await URLSession.shared.data(for: request)
    
    guard let httpResponse = response as? HTTPURLResponse,
          httpResponse.statusCode == 200 else {
        throw HubError.reconciliationFailed
    }
    
    let result = try JSONDecoder().decode(ReconcileResponse.self, from: data)
    print("Reconciliation: \(result.message), found \(result.newVideosCount) new videos")
    return result.newVideosCount
}
```

**When to Call**:
- On pull-to-refresh gesture
- When app comes to foreground (after being in background)
- After registering new channels (optional, to get immediate results)

**Important**: This is the primary mechanism for discovering videos. The server no longer automatically polls YouTube every 30 minutes. WebSub provides real-time updates for most videos, but reconciliation catches any that were missed.

**Rate Limiting**: The endpoint is rate-limited. Don't call it more than once every 15 minutes per user.

**Quota Impact**: 1 unit per channel × number of user's channels. This replaces the old automatic reconciliation which consumed 48× more quota.

**Example Usage**:
```swift
class VideoFeedManager {
    private var lastReconcile: Date?
    private let reconcileInterval: TimeInterval = 15 * 60 // 15 minutes
    
    func refreshFeed() async throws -> [Video] {
        // 1. Reconcile if needed (rate-limited)
        if shouldReconcile() {
            let newCount = try await reconcileChannels(userId: userManager.userId)
            lastReconcile = Date()
            print("Found \(newCount) new videos")
        }
        
        // 2. Fetch feed
        return try await fetchFeed(userId: userManager.userId)
    }
    
    private func shouldReconcile() -> Bool {
        guard let last = lastReconcile else { return true }
        return Date().timeIntervalSince(last) >= reconcileInterval
    }
}
```

---

### Endpoint 4: Get Feed

**Purpose**: Retrieve aggregated videos from all subscribed channels.

```
GET /api/users/{userId}/feed?since={ISO8601}&limit={number}
```

**Query Parameters**:
- `since` (optional): ISO 8601 timestamp. Returns videos published after this time.
- `limit` (optional, default: 50): Maximum number of videos to return.

**Response**:
```json
{
  "videos": [
    {
      "videoId": "dQw4w9WgXcQ",
      "channelId": "UCXuqSBlHAE6Xw-yeJA0Tunw",
      "publishedAt": "2025-12-06T10:30:00Z",
      "title": "Amazing Video Title",
      "description": "Video description here...",
      "thumbnailUrl": "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
      "duration": "00:04:13"
    },
    ...
  ],
  "nextCursor": "2025-12-06T10:00:00Z",
  "nextPageToken": null
}
```

**Note**: `nextCursor` is the new pagination field. Use it as the `since` parameter for the next request. `nextPageToken` is deprecated but kept for backwards compatibility.

**Swift Implementation**:
```swift
struct FeedResponse: Codable {
    let videos: [VideoDTO]
    let nextCursor: String?
    let nextPageToken: String? // Deprecated, use nextCursor
}

struct VideoDTO: Codable {
    let videoId: String
    let channelId: String
    let publishedAt: Date
    let title: String?
    let description: String?
    let thumbnailUrl: String?
    let duration: TimeInterval?
    
    enum CodingKeys: String, CodingKey {
        case videoId, channelId, publishedAt, title, description, thumbnailUrl, duration
    }
    
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        videoId = try container.decode(String.self, forKey: .videoId)
        channelId = try container.decode(String.self, forKey: .channelId)
        publishedAt = try container.decode(Date.self, forKey: .publishedAt)
        title = try container.decodeIfPresent(String.self, forKey: .title)
        description = try container.decodeIfPresent(String.self, forKey: .description)
        thumbnailUrl = try container.decodeIfPresent(String.self, forKey: .thumbnailUrl)
        
        // Duration comes as TimeSpan string "00:04:13"
        if let durationStr = try container.decodeIfPresent(String.self, forKey: .duration) {
            duration = Self.parseTimeSpan(durationStr)
        } else {
            duration = nil
        }
    }
    
    private static func parseTimeSpan(_ timeSpan: String) -> TimeInterval? {
        let components = timeSpan.split(separator: ":")
        guard components.count >= 2 else { return nil }
        
        if components.count == 3 {
            // HH:MM:SS
            guard let hours = Double(components[0]),
                  let minutes = Double(components[1]),
                  let seconds = Double(components[2]) else { return nil }
            return hours * 3600 + minutes * 60 + seconds
        } else {
            // MM:SS
            guard let minutes = Double(components[0]),
                  let seconds = Double(components[1]) else { return nil }
            return minutes * 60 + seconds
        }
    }
}

func fetchFeed(userId: String, since: Date? = nil, limit: Int = 50) async throws -> FeedResponse {
    var components = URLComponents(string: "\(HubConfig.baseURL)/api/users/\(userId)/feed")!
    var queryItems: [URLQueryItem] = []
    
    if let since = since {
        let iso8601 = ISO8601DateFormatter().string(from: since)
        queryItems.append(URLQueryItem(name: "since", value: iso8601))
    }
    
    queryItems.append(URLQueryItem(name: "limit", value: "\(limit)"))
    components.queryItems = queryItems
    
    let url = components.url!
    let (data, response) = try await URLSession.shared.data(from: url)
    
    guard let httpResponse = response as? HTTPURLResponse,
          httpResponse.statusCode == 200 else {
        throw HubError.fetchFailed
    }
    
    let decoder = JSONDecoder()
    decoder.dateDecodingStrategy = .iso8601
    return try decoder.decode(FeedResponse.self, from: data)
}
```

**When to Call**:
- On app launch
- When user pulls to refresh
- When app comes to foreground
- Periodically in background (if allowed)

**Pagination Pattern**:
```swift
func fetchAllNewVideos(userId: String, since: Date) async throws -> [VideoDTO] {
    var allVideos: [VideoDTO] = []
    var currentSince: Date? = since
    
    while true {
        let feed = try await fetchFeed(userId: userId, since: currentSince, limit: 100)
        allVideos.append(contentsOf: feed.videos)
        
        // Check if there's a next cursor for more results
        guard let nextCursor = feed.nextCursor else {
            // No more results
            break
        }
        
        // Parse the cursor as ISO 8601 date for next request
        if let cursorDate = ISO8601DateFormatter().date(from: nextCursor) {
            currentSince = cursorDate
        } else {
            break
        }
    }
    
    return allVideos
}
```

**Note**: The server now provides proper pagination via `nextCursor`. Use this cursor as the `since` parameter for the next request to fetch the next page of results.

---

## Implementation Guide

### Complete Integration Flow

```swift
class HubServerManager {
    private let baseURL: String
    private let userId: String
    
    init(baseURL: String = HubConfig.baseURL, userId: String) {
        self.baseURL = baseURL
        self.userId = userId
    }
    
    // MARK: - Initial Setup
    
    func initialSetup(youtubeChannelIds: [String]) async throws {
        // 1. Check server health
        let health = try await checkHealth()
        guard health.status == "healthy" else {
            throw HubError.serverUnhealthy
        }
        
        // 2. Register user's channels
        try await registerChannels(userId: userId, channelIds: youtubeChannelIds)
        
        // 3. Fetch initial feed
        let feed = try await fetchFeed(userId: userId, limit: 100)
        
        // 4. Cache locally
        await cacheFeed(feed.videos)
    }
    
    // MARK: - Refresh Flow
    
    func refreshFeed() async throws -> [VideoDTO] {
        // Get last refresh time
        let lastRefresh = UserDefaults.standard.object(forKey: "lastFeedRefresh") as? Date
            ?? Date.distantPast
        
        // Fetch only new videos
        let feed = try await fetchFeed(userId: userId, since: lastRefresh)
        
        // Update last refresh time
        UserDefaults.standard.set(Date(), forKey: "lastFeedRefresh")
        
        // Merge with cached videos
        await mergeFeedWithCache(feed.videos)
        
        return feed.videos
    }
    
    // MARK: - Background Sync
    
    func backgroundSync() async throws {
        // Quick check without full refresh
        let recentVideos = try await fetchFeed(
            userId: userId,
            since: Date().addingTimeInterval(-3600), // Last hour
            limit: 20
        )
        
        if !recentVideos.videos.isEmpty {
            // Update badge count or send local notification
            await updateUnreadCount(recentVideos.videos.count)
        }
    }
    
    // MARK: - Subscription Management
    
    func syncSubscriptions(youtubeChannelIds: [String]) async throws {
        // Always sync all channels (hub handles deduplication)
        try await registerChannels(userId: userId, channelIds: youtubeChannelIds)
    }
    
    // MARK: - Helper Methods
    
    private func cacheFeed(_ videos: [VideoDTO]) async {
        // Implement using Core Data, Realm, or similar
        // Store videos with metadata for offline access
    }
    
    private func mergeFeedWithCache(_ newVideos: [VideoDTO]) async {
        // Merge new videos with existing cache
        // Remove duplicates, update timestamps, etc.
    }
    
    private func updateUnreadCount(_ count: Int) async {
        // Update app badge, UI indicators, etc.
    }
}
```

### Error Handling

```swift
enum HubError: Error {
    case serverUnhealthy
    case registrationFailed
    case fetchFailed
    case networkError(Error)
    case decodingError(Error)
    case invalidResponse
    case userNotFound
    
    var userMessage: String {
        switch self {
        case .serverUnhealthy:
            return "Hub server is unavailable. Please try again later."
        case .registrationFailed:
            return "Could not register your channels. Please check your connection."
        case .fetchFailed:
            return "Could not fetch videos. Pull to refresh to try again."
        case .networkError:
            return "Network error. Please check your internet connection."
        case .decodingError:
            return "Data format error. Please update your app."
        case .invalidResponse:
            return "Unexpected server response. Please try again."
        case .userNotFound:
            return "User not found on server. Please re-sync your subscriptions."
        }
    }
}

// Retry logic with exponential backoff
func fetchWithRetry<T>(
    maxRetries: Int = 3,
    operation: @escaping () async throws -> T
) async throws -> T {
    var lastError: Error?
    
    for attempt in 0..<maxRetries {
        do {
            return try await operation()
        } catch {
            lastError = error
            
            if attempt < maxRetries - 1 {
                let delay = pow(2.0, Double(attempt)) // 1s, 2s, 4s
                try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
        }
    }
    
    throw lastError ?? HubError.fetchFailed
}

// Usage
let feed = try await fetchWithRetry {
    try await fetchFeed(userId: userId)
}
```

---

## Best Practices

### 1. Caching Strategy

**Local Cache**: Always cache the hub response locally.

```swift
class VideoCache {
    private let cacheURL: URL
    
    func saveFeed(_ videos: [VideoDTO]) throws {
        let data = try JSONEncoder().encode(videos)
        try data.write(to: cacheURL)
    }
    
    func loadFeed() throws -> [VideoDTO]? {
        guard FileManager.default.fileExists(atPath: cacheURL.path) else {
            return nil
        }
        let data = try Data(contentsOf: cacheURL)
        return try JSONDecoder().decode([VideoDTO].self, from: data)
    }
}

// Display cached data immediately, then refresh
func loadVideos() async {
    // 1. Show cached data
    if let cached = try? videoCache.loadFeed() {
        await MainActor.run {
            self.videos = cached
        }
    }
    
    // 2. Fetch fresh data
    do {
        let fresh = try await hubManager.refreshFeed()
        await MainActor.run {
            self.videos = fresh
        }
    } catch {
        // Keep showing cached data if refresh fails
        print("Refresh failed: \(error)")
    }
}
```

### 2. Incremental Sync

Only fetch videos since last sync:

```swift
class SyncManager {
    @AppStorage("lastSyncTimestamp") private var lastSyncTimestamp: Double = 0
    
    func sync() async throws {
        let lastSync = lastSyncTimestamp > 0 
            ? Date(timeIntervalSince1970: lastSyncTimestamp)
            : Date.distantPast
        
        let feed = try await hubManager.fetchFeed(
            userId: userManager.userId,
            since: lastSync
        )
        
        // Update timestamp
        lastSyncTimestamp = Date().timeIntervalSince1970
        
        // Process new videos
        await processNewVideos(feed.videos)
    }
}
```

### 3. Subscription Sync Frequency

Don't sync on every app launch. Use smart logic:

```swift
func shouldSyncSubscriptions() -> Bool {
    let lastSync = UserDefaults.standard.double(forKey: "lastSubscriptionSync")
    let daysSinceSync = Date().timeIntervalSince1970 - lastSync
    
    // Sync if more than 1 day has passed
    return daysSinceSync > 86400
}

func appDidBecomeActive() async {
    if shouldSyncSubscriptions() {
        let channelIds = try await fetchYouTubeSubscriptions()
        try await hubManager.syncSubscriptions(youtubeChannelIds: channelIds)
        
        UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: "lastSubscriptionSync")
    }
    
    // Always refresh feed
    try await hubManager.refreshFeed()
}
```

### 4. Handle Server Downtime

Have a fallback to direct YouTube access:

```swift
class FeedManager {
    private let hubManager: HubServerManager
    private let youtubeManager: YouTubeDirectManager
    private var useDirectYouTube = false
    
    func fetchVideos() async throws -> [VideoDTO] {
        do {
            // Try hub first
            return try await hubManager.refreshFeed()
        } catch {
            // Fallback to direct YouTube
            print("Hub unavailable, using direct YouTube")
            useDirectYouTube = true
            return try await youtubeManager.fetchVideos()
        }
    }
    
    // Periodically retry hub
    func checkHubStatus() async {
        if useDirectYouTube {
            do {
                _ = try await hubManager.checkHealth()
                useDirectYouTube = false
                print("Hub is back online")
            } catch {
                // Still down, keep using direct YouTube
            }
        }
    }
}
```

### 5. Thumbnail Loading

Thumbnails URLs are provided, but consider caching them:

```swift
class ThumbnailCache {
    static let shared = ThumbnailCache()
    private let cache = URLCache(
        memoryCapacity: 100 * 1024 * 1024, // 100 MB memory
        diskCapacity: 500 * 1024 * 1024    // 500 MB disk
    )
    
    func loadThumbnail(from url: URL) async throws -> UIImage {
        // Check memory cache
        let request = URLRequest(url: url)
        if let cached = cache.cachedResponse(for: request),
           let image = UIImage(data: cached.data) {
            return image
        }
        
        // Download
        let (data, response) = try await URLSession.shared.data(from: url)
        let cachedResponse = CachedURLResponse(response: response, data: data)
        cache.storeCachedResponse(cachedResponse, for: request)
        
        guard let image = UIImage(data: data) else {
            throw ThumbnailError.invalidImage
        }
        
        return image
    }
}
```

### 6. Video Duration Formatting

Convert TimeInterval to human-readable format:

```swift
extension TimeInterval {
    var formattedDuration: String {
        let hours = Int(self) / 3600
        let minutes = (Int(self) % 3600) / 60
        let seconds = Int(self) % 60
        
        if hours > 0 {
            return String(format: "%d:%02d:%02d", hours, minutes, seconds)
        } else {
            return String(format: "%d:%02d", minutes, seconds)
        }
    }
}

// Usage
Text(video.duration?.formattedDuration ?? "")
```

---

## Testing

### Unit Tests

```swift
class HubServerManagerTests: XCTestCase {
    var manager: HubServerManager!
    
    override func setUp() {
        super.setUp()
        manager = HubServerManager(
            baseURL: "http://localhost:5000",
            userId: "test-user"
        )
    }
    
    func testHealthCheck() async throws {
        let health = try await manager.checkHealth()
        XCTAssertEqual(health.status, "healthy")
    }
    
    func testRegisterChannels() async throws {
        let channelIds = ["UCtest1", "UCtest2"]
        try await manager.registerChannels(userId: "test", channelIds: channelIds)
        // Verify no exception thrown
    }
    
    func testFetchFeed() async throws {
        // First register some channels
        try await manager.registerChannels(userId: "test", channelIds: ["UCtest1"])
        
        // Then fetch feed
        let feed = try await manager.fetchFeed(userId: "test")
        XCTAssertNotNil(feed)
    }
}
```

### Integration Tests

```swift
class HubIntegrationTests: XCTestCase {
    func testCompleteFlow() async throws {
        let userId = "integration-test-\(UUID().uuidString)"
        let manager = HubServerManager(userId: userId)
        
        // 1. Health check
        let health = try await manager.checkHealth()
        XCTAssertEqual(health.status, "healthy")
        
        // 2. Register channels
        let channelIds = ["UCXuqSBlHAE6Xw-yeJA0Tunw"] // Real channel
        try await manager.registerChannels(userId: userId, channelIds: channelIds)
        
        // 3. Wait a bit for server to process
        try await Task.sleep(nanoseconds: 2_000_000_000)
        
        // 4. Fetch feed
        let feed = try await manager.fetchFeed(userId: userId)
        
        // Should have videos (eventually)
        // Note: May be empty if channel hasn't published recently
        print("Fetched \(feed.videos.count) videos")
    }
}
```

### Mock Server for Testing

```swift
class MockHubServer {
    static func startMockServer() -> URLProtocol.Type {
        class MockURLProtocol: URLProtocol {
            override class func canInit(with request: URLRequest) -> Bool {
                return request.url?.host == "mock.hub.local"
            }
            
            override class func canonicalRequest(for request: URLRequest) -> URLRequest {
                return request
            }
            
            override func startLoading() {
                let response = HTTPURLResponse(
                    url: request.url!,
                    statusCode: 200,
                    httpVersion: nil,
                    headerFields: ["Content-Type": "application/json"]
                )!
                
                let json: Data
                if request.url?.path == "/health" {
                    json = """
                    {"status":"healthy","timestamp":"2025-12-06T12:00:00Z","stats":{"channels":10,"users":5,"videos":50}}
                    """.data(using: .utf8)!
                } else if request.url?.path.contains("/feed") {
                    json = """
                    {"videos":[],"nextPageToken":null}
                    """.data(using: .utf8)!
                } else {
                    json = """
                    {"message":"Success"}
                    """.data(using: .utf8)!
                }
                
                client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
                client?.urlProtocol(self, didLoad: json)
                client?.urlProtocolDidFinishLoading(self)
            }
            
            override func stopLoading() {}
        }
        
        return MockURLProtocol.self
    }
}

// Usage in tests
let config = URLSessionConfiguration.ephemeral
config.protocolClasses = [MockHubServer.startMockServer()]
let session = URLSession(configuration: config)
```

---

## Migration Checklist

### Pre-Migration

- [ ] Set up hub server (see DEPLOYMENT.md)
- [ ] Verify server is accessible from production
- [ ] Test API endpoints manually
- [ ] Configure monitoring and logging

### Code Changes

- [ ] Add HubServerManager class
- [ ] Implement health check
- [ ] Implement channel registration
- [ ] Implement feed fetching
- [ ] Add local caching layer
- [ ] Add error handling and retry logic
- [ ] Add fallback to direct YouTube (optional)
- [ ] Update UI to show sync status

### Testing

- [ ] Write unit tests for API calls
- [ ] Test with real YouTube channels
- [ ] Test offline behavior
- [ ] Test server downtime scenario
- [ ] Performance test with large channel lists
- [ ] Beta test with real users

### Deployment

- [ ] Feature flag for hub vs direct YouTube
- [ ] Gradual rollout to subset of users
- [ ] Monitor error rates and performance
- [ ] Full rollout once stable
- [ ] Remove direct YouTube code (later)

---

## Troubleshooting

### Videos Not Appearing

**Symptom**: Feed returns empty array even after registering channels.

**Causes**:
1. Channels haven't published videos recently
2. WebSub hasn't received notifications yet
3. **Reconciliation hasn't been triggered yet (app must call reconciliation endpoint)**

**Solution**:
- **Call the reconciliation endpoint**: `POST /api/users/{userId}/reconcile`
- Wait for WebSub to deliver notifications (usually within seconds of video publish)
- Check hub server logs
- Verify channels are actually active on YouTube

### "User not found" Error

**Symptom**: GET /feed returns 404.

**Cause**: User ID not registered or channels not added.

**Solution**:
```swift
// Always register channels before fetching feed
try await manager.registerChannels(userId: userId, channelIds: channelIds)
try await manager.fetchFeed(userId: userId)
```

### Stale Data

**Symptom**: Videos are old, not seeing recent uploads.

**Causes**: 
1. App is showing cached data without refreshing
2. **Not calling reconciliation endpoint on pull-to-refresh**

**Solution**:
- **Always call reconciliation endpoint on pull-to-refresh**: `POST /api/users/{userId}/reconcile`
- Use `since` parameter with last refresh time when fetching feed
- Pull-to-refresh should: 1) reconcile, then 2) fetch with `since = lastRefreshTime`
- Don't rely only on cached data

### Performance Issues

**Symptom**: Feed loading is slow.

**Causes**:
1. Fetching too many videos at once
2. Not using local cache
3. Loading all thumbnails synchronously

**Solutions**:
- Reduce `limit` parameter (try 50-100)
- Implement aggressive local caching
- Load thumbnails lazily as user scrolls
- Use thumbnail placeholders

---

## Advanced Topics

### Push Notifications (Future)

The hub server could be extended to send push notifications:

```swift
// Server-side (future implementation)
app.MapPost("/api/users/{userId}/register-device", async (
    string userId,
    DeviceRegistration registration) => {
    // Store APNs token
    // Send push when new videos arrive
});

// Client-side
func registerForPushNotifications(deviceToken: Data) async throws {
    let token = deviceToken.map { String(format: "%02.2hhx", $0) }.joined()
    // Send to hub server
}
```

### CloudKit Sync

Sync watched/skipped status across devices using CloudKit:

```swift
class WatchedStatusSync {
    func markAsWatched(videoId: String) async throws {
        // 1. Update local database
        localDB.markWatched(videoId)
        
        // 2. Sync to CloudKit
        let record = CKRecord(recordType: "WatchedVideo")
        record["videoId"] = videoId
        record["userId"] = userManager.userId
        record["watchedAt"] = Date()
        
        try await cloudKitDB.save(record)
    }
}
```

### Offline Support

Cache videos for offline viewing:

```swift
class OfflineManager {
    func downloadVideo(videoId: String) async throws {
        // 1. Get video metadata from hub
        let feed = try await hubManager.fetchFeed(userId: userId)
        guard let video = feed.videos.first(where: { $0.videoId == videoId }) else {
            throw OfflineError.videoNotFound
        }
        
        // 2. Download video using yt-dlp or similar
        // (This requires additional work and YouTube TOS compliance)
        
        // 3. Store locally with metadata
        try localDB.saveOfflineVideo(video)
    }
}
```

---

## Summary

The MeTube Hub Server provides a significant improvement over direct YouTube API access by:

1. **Reducing quota usage** through shared caching
2. **Providing real-time updates** via WebSub
3. **Simplifying client code** by handling subscriptions server-side
4. **Enabling new features** like push notifications and better sync

The migration is straightforward:
1. Keep existing YouTube OAuth for user authentication
2. Send channel IDs to hub instead of fetching videos directly
3. Fetch aggregated feed from hub
4. Cache locally for performance

With proper error handling and caching, the app will be more reliable, faster, and use less of YouTube's quota.
