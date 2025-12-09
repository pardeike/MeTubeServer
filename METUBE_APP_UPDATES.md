# MeTube App Integration Updates Required

This document outlines the changes needed in the MeTube iOS/tvOS app (pardeike/MeTube) to work with the new on-demand reconciliation architecture.

## Summary of Server Changes

The MeTube Hub Server has been updated to use **on-demand reconciliation** instead of automatic background polling. This dramatically reduces YouTube API quota usage while maintaining data freshness.

**Key Change**: The server no longer automatically checks for new videos every 30 minutes. Instead, the app must trigger reconciliation by calling a new API endpoint.

---

## Required App Changes

### 1. Add New API Endpoint Call

#### New Endpoint
```
POST https://your-hub-server.com/api/users/{userId}/reconcile
```

#### Response
```json
{
  "message": "Reconciliation completed",
  "newVideosCount": 5
}
```

#### Swift Implementation
Add this method to your API client:

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
    return result.newVideosCount
}
```

---

### 2. Update Pull-to-Refresh Logic

**Before**:
```swift
func refreshFeed() async throws -> [Video] {
    // Just fetch the feed
    return try await fetchFeed(userId: userManager.userId)
}
```

**After**:
```swift
func refreshFeed() async throws -> [Video] {
    // 1. First, reconcile to check for new videos
    let newCount = try await reconcileChannels(userId: userManager.userId)
    print("Found \(newCount) new videos")
    
    // 2. Then fetch the updated feed
    return try await fetchFeed(userId: userManager.userId)
}
```

---

### 3. Add Foreground Transition Handler

Call reconciliation when the app comes to foreground:

```swift
class AppLifecycleManager: ObservableObject {
    private var lastReconcile: Date?
    private let reconcileInterval: TimeInterval = 15 * 60 // 15 minutes
    
    init() {
        // Listen for app becoming active
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(appDidBecomeActive),
            name: UIApplication.didBecomeActiveNotification,
            object: nil
        )
    }
    
    @objc private func appDidBecomeActive() {
        Task {
            await reconcileIfNeeded()
        }
    }
    
    private func reconcileIfNeeded() async {
        // Rate limit: only reconcile if it's been 15+ minutes
        if shouldReconcile() {
            do {
                let newCount = try await reconcileChannels(userId: userManager.userId)
                lastReconcile = Date()
                print("Background reconciliation found \(newCount) new videos")
            } catch {
                print("Background reconciliation failed: \(error)")
            }
        }
    }
    
    private func shouldReconcile() -> Bool {
        guard let last = lastReconcile else { return true }
        return Date().timeIntervalSince(last) >= reconcileInterval
    }
}
```

---

### 4. Rate Limiting Best Practices

**Important**: The reconciliation endpoint is rate-limited by the server. Follow these guidelines:

- **Minimum interval**: Don't call more than once every 15 minutes per user
- **Store last reconciliation time**: Use UserDefaults or in-memory cache
- **Handle errors gracefully**: If reconciliation fails, still show cached data

```swift
class ReconciliationManager {
    private static let minInterval: TimeInterval = 15 * 60 // 15 minutes
    private static let lastReconcileKey = "lastReconcileTimestamp"
    
    static func canReconcile() -> Bool {
        guard let lastTimestamp = UserDefaults.standard.object(forKey: lastReconcileKey) as? Date else {
            return true
        }
        return Date().timeIntervalSince(lastTimestamp) >= minInterval
    }
    
    static func recordReconciliation() {
        UserDefaults.standard.set(Date(), forKey: lastReconcileKey)
    }
    
    static func reconcileIfAllowed(userId: String) async throws -> Int? {
        guard canReconcile() else {
            print("Reconciliation skipped: rate limited")
            return nil
        }
        
        let count = try await reconcileChannels(userId: userId)
        recordReconciliation()
        return count
    }
}
```

---

### 5. Update Error Handling

Add a new error case:

```swift
enum HubError: Error {
    case serverUnhealthy
    case registrationFailed
    case reconciliationFailed  // NEW
    case fetchFailed
    case invalidResponse
}
```

---

### 6. Update UI Feedback (Optional but Recommended)

Show reconciliation status to users:

```swift
@MainActor
class FeedViewModel: ObservableObject {
    @Published var isReconciling = false
    @Published var lastReconcileTime: Date?
    @Published var newVideosFound: Int = 0
    
    func refresh() async {
        isReconciling = true
        defer { isReconciling = false }
        
        do {
            // Reconcile
            if let count = try await ReconciliationManager.reconcileIfAllowed(userId: userId) {
                newVideosFound = count
                lastReconcileTime = Date()
            }
            
            // Fetch feed
            let videos = try await fetchFeed(userId: userId)
            self.videos = videos
            
            // Show toast if new videos found
            if newVideosFound > 0 {
                showToast("Found \(newVideosFound) new videos!")
            }
        } catch {
            showError("Refresh failed: \(error.localizedDescription)")
        }
    }
}
```

---

## Testing Checklist

After implementing these changes, test:

- [ ] Pull-to-refresh calls reconciliation endpoint
- [ ] App foreground transition triggers reconciliation (if 15+ min since last)
- [ ] Rate limiting prevents excessive calls
- [ ] New videos appear after reconciliation
- [ ] App works gracefully if reconciliation fails
- [ ] UI shows appropriate loading states

---

## Migration Timeline

### Immediate (Required for Server v2.0+)
- Add reconciliation endpoint call
- Update pull-to-refresh logic
- Add foreground transition handler

### Optional Improvements
- Add UI feedback for reconciliation status
- Show "X new videos" after reconciliation
- Add settings toggle for reconciliation frequency

---

## Benefits of This Change

1. **Massive quota reduction**: Server quota usage drops by ~80-90%
2. **Better control**: App decides when to check for new videos
3. **Same user experience**: Pull-to-refresh still works, just triggers reconciliation first
4. **More reliable**: WebSub still provides real-time updates for most videos

---

## Questions?

If you have questions about these changes, refer to:
- `CLIENT.md` in MeTubeServer repository for detailed API documentation
- `QUOTA_FUNCTIONS.md` for quota analysis
- Server endpoint: `POST /api/users/{userId}/reconcile`

---

## Example Complete Implementation

Here's a complete example of an updated feed manager:

```swift
class HubFeedManager {
    private let baseURL = "https://your-hub-server.com"
    private let userId: String
    private var lastReconcile: Date?
    private let reconcileInterval: TimeInterval = 15 * 60
    
    init(userId: String) {
        self.userId = userId
        setupLifecycleObserver()
    }
    
    // MARK: - Public API
    
    func refreshFeed() async throws -> [Video] {
        // 1. Reconcile if allowed
        if shouldReconcile() {
            let newCount = try await reconcileChannels()
            print("Reconciliation found \(newCount) new videos")
            lastReconcile = Date()
        }
        
        // 2. Fetch feed
        return try await fetchFeed()
    }
    
    // MARK: - Private Methods
    
    private func reconcileChannels() async throws -> Int {
        let url = URL(string: "\(baseURL)/api/users/\(userId)/reconcile")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        
        let (data, _) = try await URLSession.shared.data(for: request)
        let result = try JSONDecoder().decode(ReconcileResponse.self, from: data)
        return result.newVideosCount
    }
    
    private func fetchFeed() async throws -> [Video] {
        let url = URL(string: "\(baseURL)/api/users/\(userId)/feed?limit=50")!
        let (data, _) = try await URLSession.shared.data(from: url)
        let result = try JSONDecoder().decode(FeedResponse.self, from: data)
        return result.videos
    }
    
    private func shouldReconcile() -> Bool {
        guard let last = lastReconcile else { return true }
        return Date().timeIntervalSince(last) >= reconcileInterval
    }
    
    private func setupLifecycleObserver() {
        NotificationCenter.default.addObserver(
            forName: UIApplication.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task {
                try? await self?.refreshFeed()
            }
        }
    }
}

struct ReconcileResponse: Codable {
    let message: String
    let newVideosCount: Int
}

struct FeedResponse: Codable {
    let videos: [Video]
    let cursor: String?
}
```

---

This implementation ensures your app works seamlessly with the new on-demand reconciliation architecture while providing a great user experience.
