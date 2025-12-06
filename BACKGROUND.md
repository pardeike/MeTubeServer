# My original question

Let’s entertain the idea of my own server of my app MeTube (https://github.com/pardeike/MeTube). Because having all the API usage in the app simply stressed the quota too much. The app is designed to give a user a complete and updated list of all the videos of all their subscriptions. Locally it has functions like sorting and filtering videos based on watched/skipped status as well as view subscribed channels on their own with all videos. It will have a player that plays locally on an iPhone or stream to AppleTV. In the future I want that app on AppleTV too and the video status info will be synced via CloudKit. So the server has mainly the role of supplying all the meta data and thumbnails in an efficient way to the app. The app will want to cache the server response and only get new info at start (or when taken to the foreground) so the app-server protocol needs to have a way to catch up quickly. The server itself needs to serve multiple users of the app.

So I asked ChatGPT: Based on PubSubHubbub/WebSub, will it work and not miss stuff even if I restart the server or the serve being down for hours? What is the minimum state I need to store there? I guess some credentials or can I skip that? I get that subbing will give me all new videos for a channel so if I have multiple users then each shared channel is less work for me. Can this be done in a responsible way? Sketch me an optimal design that I can either run at home or in the cloud (preferably Cloudflare or GitHub)

# ChatGPT's idea

# MOTIVATION: Practical takeaway for your use case

Below is an idea you could actually implement.

## 1. What happens if the server is down?
Mechanics:
You subscribe with:
hub.mode=subscribe
hub.topic=https://www.youtube.com/feeds/videos.xml?channel_id=CHANNEL_ID
hub.callback=https://your-server/websub/youtube
optional: hub.secret, hub.lease_seconds
to the Google hub at https://pubsubhubbub.appspot.com/ (the “Google hub”). 
Google for Developers

The hub verifies by doing GET to your callback with hub.mode, hub.topic, hub.challenge, hub.lease_seconds. You must respond 200 with the body exactly equal to hub.challenge.
pubsubhubbub.github.io

When a video changes (new upload or title/description update), the hub POSTs an Atom feed fragment with <yt:videoId> and <yt:channelId> to your callback. 
Google for Developers

Downtime:
Spec says hubs “should retry” delivery a few times on failure, but there’s no guarantee of long-term queuing. If you’re offline for hours, some notifications can be lost. 
groups.google.com
Subscriptions are time-limited. The hub returns hub.lease_seconds and you’re expected to resubscribe before it expires (max seen for Google hub is about 828 000 s ≈ 9.6 days). 
Stack Overflow

Conclusion: WebSub alone is not enough for correctness. You need a polling backstop that reconciles your state after any downtime or suspected loss.

## 2. Minimum state you really need
At the server you can get away with:
Per channel (global)
channel_id
topic_url (YouTube feed URL)
uploads_playlist_id (from Data API, for reconciliation)
lease_expires_at (from hub.lease_seconds)
hub_secret (if you use HMAC)
last_seen_published_at or last_seen_video_id (what you’ve already processed)
Per user
user_id (your own internal ID)
channel_id
user_last_seen_video_id (optional, if you want per-user “unread” state)
That’s it. You don’t need to store OAuth tokens on the server if you let the iOS app talk directly to YouTube for “mine=true” endpoints and just POST you the channel IDs.
For security:
Use hub.secret so the hub signs the payload with HMAC and you can verify it. You must store that per subscription. 
pubsubhubbub.appspot.com
Credentials:
WebSub itself needs no YouTube OAuth.
Data API calls from your server (for metadata / reconciliation) can just use an API key, since they’re channel/video lookups, not “mine=true” user calls.

## 3. High-level data flow for many users
3.1. iOS app responsibilities
User signs in with Google OAuth (YouTube scope).
App calls YouTube Data API directly:
subscriptions.list(mine=true) to get all channelIds.
App POSTs to your backend:
POST /api/register-subscriptions
{
  "user_id": "some-stable-id",
  "channels": [
    "UC123…",
    "UC456…"
  ]
}
No secrets here, just your auth (JWT / session) so nobody can spam your backend.
3.2. Backend responsibilities
On register-subscriptions:
For each channel_id:
If channel unknown:
Fetch uploads_playlist_id via channels.list(part=contentDetails, id=channel_id).
Insert into channels table.
Create a WebSub subscription for that channel if there isn’t one.
Insert (or upsert) row in user_channels mapping.
On WebSub callback:
GET → verification:
Find channel_id by matching hub.topic.
Update lease_expires_at = now + lease_seconds.
Return 200 with body hub.challenge.
POST → notification:
Verify HMAC with hub_secret if set.
Parse Atom:
For each <entry> get yt:videoId, yt:channelId, published.
For each video:
If published <= last_seen_published_at[channel_id] or video_id already seen → ignore (duplicate or older edit).
Else:
Store/update video record.
Update last_seen_published_at[channel_id].
Optionally call videos.list (batched up to 50 IDs) to enrich metadata.
Mark “these videos are new” for all users following that channel_id (e.g., insert into user_new_videos).
The iOS app then either:
Polls GET /api/new-videos?since=… and you return aggregated results, or
You send APNs notifications out of band.

## 4. Handling downtime responsibly
Key idea: treat WebSub as a hint, and on top of it run a small reconciliation job.
4.1. Subscription maintenance
Background job every N minutes (cron / worker):
For each channel:
If now > lease_expires_at - safety_margin (e.g. minus 1 day):
Re-POST hub.mode=subscribe to the hub for that topic and callback.
That keeps subscriptions alive without user interaction. 
pubsubhubbub.appspot.com

4.2. Reconciliation / backfill
Also periodically (or on startup):
For each channel:
playlistItems.list on the uploads playlist:
Use publishedAfter = last_seen_published_at[channel_id] (or fetch the last N items and filter client-side).
For any video newer than last_seen_published_at that you haven’t seen in WebSub:
Insert it as if it came from WebSub.
Update last_seen_published_at.
That covers:
Your server being down when WebSub tried to POST.
The hub having issues (there have been delays/outages reported for YouTube’s hub). 
issuetracker.google.com

Worst case, you miss a push and catch it on the next reconciliation cycle.

## 5. Multi-user & quota behaviour
WebSub subscription is per channel, not per user.
One topic subscription for UC123… no matter how many users follow it.
When the last user unsubscribes from that channel in your app, you can send hub.mode=unsubscribe and drop it.
YouTube Data API quota from the server side remains small:
Occasional channels.list (1 unit per call).
Batching playlistItems.list reconciliation per channel.
videos.list batched by 50 video IDs.
And because you only reconcile channels that have followers, the cost scales with the union of channels, not with (users × channels).

## 6. Concrete deployment options
Option A: Cloudflare Workers (recommended for you)
Pieces:
Worker script exposing:
POST /websub/youtube and GET /websub/youtube (same route, different methods) for callbacks.
POST /api/register-subscriptions for the app.
Optional GET /api/new-videos.
Storage:
Cloudflare D1 for:
channels (schema as above)
user_channels
videos
Or KV if you want something simpler and don’t care about complex queries.
Cron Triggers:
Worker cron job that:
Renews subscriptions nearing lease expiry.
Runs reconciliation against Data API.
Pros:
Globally reachable HTTPS endpoint, no home-network hassle.
Cheap / free tier is enough for a personal app.
No servers to patch.
Option B: Home server behind Cloudflare Tunnel
Run a tiny Go/Node/Rust service in Docker at home.
Put it behind Cloudflare Tunnel (gives you a public HTTPS URL that points to your LAN service).
Same logic as above (callback endpoints + cron job), persistence via SQLite/Postgres on disk.
Pros:
Full control, easy debugging.
Can reuse the machine for other hobby services.
Cons:
If your home network is down for a while, you rely on the reconciliation to catch up.
You must keep the tunnel/container running.
Why not GitHub?
GitHub Pages is static only → cannot receive WebSub callbacks.
GitHub Actions can’t easily act as a public, always-on webhook endpoint. You’d be fighting the platform.
So Cloudflare (Workers/Pages with Functions) or a small VPS/home server is the sane choice.

## 7. Summary “optimal” design
Push:
One WebSub subscription per channel, stored and renewed centrally.
HMAC secrets to verify notifications.
Atom POST → parse → update per-channel last_seen_published_at + per-user “new” sets.
Pull:
iOS app does user OAuth and sends only channel IDs to the backend.
Backend occasionally reconciles via playlistItems.list to cover gaps.
State:
channels + user_channels + optional videos table.
No user OAuth secrets stored server-side unless you want to.
That gives you:
Low quota usage.
No per-user polling storm.
Reasonable correctness even with outages.
Easy scaling from “one user, one server at home” to “several users using a Cloudflare Worker”.

# IMPLEMENTATION 
See DESIGN.md
