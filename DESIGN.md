# Watchblox — Design Document

**Tagline:** Know when your friends play.
**Platform:** WPF / .NET 8, Windows 10/11, normal user (no admin).
**Repo:** https://github.com/omggSarahhwastaken/Watchblox (public)
**Distribution:** GitHub Releases + in-app updater (same manifest pattern as Sarah's Toolkit).
**Status:** v1.0.0 MVP — building 2026-09-28.

Public-facing material never mentions VRCX or AI authorship. No telemetry, no accounts, no tracking.

---

## 1. What it is

A Roblox friend & presence companion. You enter your Roblox username once.
Watchblox resolves it, pulls your public friend list, and keeps a live
dashboard: who's offline / online / in game / in Studio, what game each person
is in, an activity feed of transitions, Windows toast notifications, and a
**Join** button that drops you straight into an in-game friend's exact server —
no browser, no extension.

Login is never required for the core app. An optional cookie-based login
arrives in Phase 2 for guaranteed friend-list access on private profiles,
pending friend requests, and write actions.

---

## 2. MVP feature list (v1.0.0)

1. **First-run onboarding** — enter Roblox username → resolve to ID → confirm
   identity with avatar + display name + @username. One tracked account.
   (Support for switching accounts later, not in MVP.)
2. **Presence dashboard** — friend rows with avatar headshot, display name,
   @username, verified badge, status dot, game name under in-game friends.
   Status filter chips: All / Online / In Game / Offline. Live search box.
   Sort: status first (in game → online → offline), then name.
3. **Presence polling** — every 60s default (30/60/120 in settings), IDs chunked
   at 100 per call. Compares against previous state, emits transitions only.
4. **Activity feed** — chronological transitions: came online, went offline,
   joined *Game*, switched to *Game*, left game (back to online), joined
   Studio. Relative timestamps. Capped at 500 events, persisted locally.
5. **Windows toast notifications** — on the same transitions. Master toggle in
   settings. (Per-type toggles: Phase 2.)
6. **Join button** — on every in-game friend row. Fires
   `roblox://placeId={placeId}&gameInstanceId={gameId}` through the shell.
   Both values come straight from presence — no server scanning needed.
   Fallback: open the game page in the browser. If Roblox Player isn't
   installed, explain that instead of failing silently.
7. **Manual watchlist** — add any Roblox username (non-friends, alt accounts,
   rivals). Same presence tracking, badged as "Watchlist".
8. **Friend sync** — every 15 min: re-fetch friend list (paginated, 1000 cap),
   resolve unknown IDs via batch users (chunked 100), refresh avatars for new
   faces. New friends → feed event. Removed friends → quietly dropped
   (no drama event; they're just gone from the list).
9. **Name cache** — ID → (username, displayName, verified) persisted; only
   unknown IDs hit the API. Re-resolve display names weekly (people rename).
10. **Thumbnail cache** — avatar headshots cached on disk
    (%LocalAppData%/Watchblox/thumbnails/), keyed by user ID + version.
11. **Settings** — poll interval, toast master toggle, start with Windows,
    minimize to tray, interface scale (reuse Toolkit pattern), persisted with
    the same atomic-write hardening as Toolkit v2.6.0.
12. **Updater** — gist manifest → GitHub Release asset → installer, same flow
    as the Toolkit. Footer magic `WBXINSTL`.

### Explicitly NOT in MVP
- Game icons / live player counts (Phase 2)
- Mutual friends view (Phase 2, needs design below)
- Login / cookie / friend requests (Phase 2)
- Per-friend notification toggles (Phase 2)
- Multiple tracked accounts (later)

---

## 3. Verified API map (live-tested 2026-09-28, no cookie)

| Purpose | Call |
|---|---|
| Username → ID | `POST users.roblox.com/v1/usernames/users` `{"usernames":[...]}` |
| Friend list | `GET friends.roblox.com/v1/users/{id}/friends` (cursor pages) |
| Presence | `POST presence.roblox.com/v1/presence/users` `{"userIds":[...]}` max 100 |
| ID → names | `POST users.roblox.com/v1/users` `{"userIds":[...]}` max 100 |
| Avatars | `GET thumbnails.roblox.com/v1/users/avatar-headshot?userIds=...&size=150x150&format=Png&isCircular=false` max 100 |
| Game metadata | `GET games.roblox.com/v1/games?universeIds=...` max 50 |
| Game icons | `GET thumbnails.roblox.com/v1/games/icons?universeIds=...` |

Presence fields (confirmed): `userPresenceType` (0 offline, 1 online,
2 in game, 3 in Studio), `lastLocation` (game name string, may be hidden by
privacy), `placeId`, `rootPlaceId`, `gameId` (server instance GUID — this is
what powers Join), `universeId` (given directly, no place→universe chain
needed).

**Gotchas baked into the design:**
- Friend records may carry IDs with empty name fields → always enrich via
  batch users.
- Followers/followings endpoints return 401 without a cookie → never depend
  on them in MVP.
- `lastLocation` can be empty while in game (privacy) → row shows "In Game"
  with no game name rather than guessing.
- 429 → exponential backoff with jitter; status bar shows "Rate limited —
  retrying" instead of erroring out.
- The game-metadata endpoint shape is well documented but wasn't live-verified
  (no in-game test user available); the app treats its failure as
  non-fatal and falls back to `lastLocation` text.

---

## 4. Join — the killer feature

Browser extensions find arbitrary players by scanning server lists with
anonymized playerTokens and matching avatar thumbnails. We don't need any of
that: presence already returns each in-game friend's exact `gameId` every
poll. Join is one shell call:

```
roblox://placeId={placeId}&gameInstanceId={gameId}
```

C# side: `Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })`.
Requires Roblox Player installed (it registers the protocol). Failure modes
(full server, private server, joins disabled) → toast explaining + offer to
open the game page instead.

Phase 2 option: arbitrary-player server scan via playerTokens for
non-friend lookups (paginated `servers/Public`, token→thumbnail matching).
Not needed for friends. Ever.

---

## 5. Mutual friends (Phase 2)

No official Roblox mutual-friends endpoint — compute client-side by set
intersection. Friend cap is 1000 (raised 2024), so a full sweep is expensive:
**never on a timer.** Design:

- Compute lazily when opening a person's detail view; cache the result.
- One slow staggered background sweep at most hourly, only while the app is
  open and idle.
- UI: "N mutual friends" on the profile view, expandable avatar list;
  "People you may know" (friends-of-friends ranked by mutual count).
- Login (Phase 2) guarantees *your* list even on private profiles; without
  login it still works if your profile is public.

---

## 6. Phase 2 login design (cookie, strict)

- Paste-cookie flow only (never scrape the browser). One password-style box,
  "paste your .ROBLOSECURITY".
- Stored with DPAPI `ProtectedData`, `CurrentUser` scope, entropy salt,
  base64 in settings. Never logged, never in URLs, memory-only handler.
- Honest limits (shown in-app, one line): protects against other Windows
  users and offline file theft, not against malware running as you.
- Cookie dies on password change / logout → API 401 → clear it, prompt to
  re-paste. Never retry blindly with a dead cookie.
- Unlocks: guaranteed own friend list, pending friend requests inbox,
  followers/followings, and (later, explicit per-action confirm) write
  actions like friend requests. Exact-server join already works without it.

---

## 7. UI design

Same visual family as Sarah's Toolkit: dark theme, Minecraft Bold Italic for
the "Watchblox" title, Minecraft Italic for page headings, Segoe UI for body.
Accent: Roblox-ish red? No — keep the Toolkit's purple neon identity so the
two apps feel like siblings. Only white in the UI is text.

**Layout (original implementation, VRCX-style information layout):**
- Left sidebar (dark): tracked-user card at top (avatar, display name,
  friend count, status), nav below: Friends / Activity / Watchlist /
  Settings. Update badge when an update is available.
- Friends page: filter chips + search on top; rows = avatar circle, status
  dot, display name (+ verified check), @username dimmed, game name as
  second line for in-game ("🕹 War Tycoon" — no emoji, use text/icon),
  Join button appears on hover/selection for in-game friends.
- Friend detail (click a row): big avatar, names, status, current game with
  Join button, "added X" date, Phase 2: mutual friends.
- Activity page: reverse-chron feed, icon per event type, relative time,
  "Clear" button.
- Watchlist page: same rows, badged; add-box at top (username → resolve →
  confirm → add); remove on hover.
- Settings page: plain rows, same control style as Toolkit.
- First-run: centered card, username box, resolving spinner, identity
  confirm card, "Start watching" button. No dead ends: every error state
  has a next step written in plain language.

**Empty states with personality:** "No friends online right now." /
"No activity yet — it'll show up here when friends come and go."

---

## 8. Architecture

Single WPF project, `Watchblox/`:

- `Services/RobloxApiClient.cs` — one shared HttpClient, no cookies in MVP;
  all endpoint wrappers; chunking helpers; 429 backoff.
- `Services/PresenceMonitor.cs` — timer loop, previous-state dict,
  transition events.
- `Services/FriendSyncService.cs` — 15-min friend list sync, name backfill.
- `Services/NameCache.cs`, `Services/ThumbnailCache.cs` — memory + disk.
- `Services/ActivityStore.cs` — append-only JSON, cap 500, pruned on write.
- `Services/ToastService.cs` — Windows toasts
  (Microsoft.Toolkit.Uwp.Notifications).
- `Services/SettingsService.cs` — atomic JSON writes (Toolkit v2.6.0 pattern).
- `Services/UpdateService.cs` — gist manifest → release asset (port).
- `Models/` — TrackedUser, FriendEntry, PresenceSnapshot, ActivityEvent,
  GameInfo, AppSettings.
- Storage root: `%LocalAppData%/Watchblox/` — settings.json (+ .bak),
  activity.json, names.json, thumbnails/.

**Polling budget (100 tracked friends):** presence 1 call/min; friend sync
~10 calls/15 min; name backfill only for unknown IDs. Well under limits.

---

## 9. Edge cases & failure behavior

- **Private profile:** friends endpoint 403/empty → onboarding explains,
  offers watchlist-only mode. App stays useful.
- **Wrong username:** resolve returns nothing → "Couldn't find that
  username — check the spelling."
- **Username changed:** we track by ID; display names re-resolved weekly.
- **Banned/deleted friend:** batch lookup errors → row greyed "Unavailable",
  kept until next sync then dropped with no event.
- **Offline at startup:** load cache immediately, banner "Couldn't reach
  Roblox — showing last known. Retrying…", auto-recover.
- **Flapping (online/offline/online):** record all transitions; no debounce
  in MVP (VRCX-style truthful feed). Revisit if noisy.
- **Clock:** all timestamps local, relative in feed.
- **Uninstall:** installer removes app; data dir left behind with a note
  (or clean it — Toolkit precedent: installer wipes install dir; user data
  in LocalAppData is standard to leave).

---

## 10. Release engineering

- Version in csproj `<Version>` only; footer magic `WBXINSTL` (8 bytes).
- Self-contained single-file win-x64 publish → installer via the Toolkit's
  `build_installer.py` (parameterized for app name) → GitHub Release asset.
- Update manifest: **new secret gist** `watchblox-version.json`
  (version, url, notes, encoding raw).
- No Discord update channel until the user sets one up — no announce.
- Repo README: what it is, screenshot (after first Windows run), download
  link to latest release, "no login needed" note. No VRCX mention. No AI
  mention.

---

## 11. Open questions for the user

1. Default poll interval: 60s (safer) or 30s (snappier)?
2. Toast toggles per event type in MVP, or master switch only?
3. Keep Phase 2 login on the roadmap, or cut it entirely for now?
4. Sidebar accent: Toolkit purple, or its own color so the apps are
   distinguishable at a glance?
