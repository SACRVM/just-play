# JustPlay 0.3.1

A bugfix & stability release — smoother playback, snappier seeking, and a proper crash safety net.

## Playback
- **Auto-play on open** — double-clicking a track (or opening one) now plays it right away, like every other player.
- **Like / Analyze / Write-tags no longer interrupt the playing song.** The tag write is deferred and lands the moment you move to the next track (or when you quit) — playback never stops.
- **Bulk-analyzing a playlist no longer jumps back to the first track.**
- **Snappier seeking** — scrubbing reacts instantly now (especially noticeable on FLAC).

## Polish
- The app is now **`JustPlay.exe`** and shows up as **“JustPlay”** in Windows context menus / “Open with” (was the internal “JustPlay.App”).
- A long loaded-playlist name no longer overflows the queue header — the name lives in the “…” menu; the header stays clean.

## Stability
- **Never-crash safety net.** Unexpected errors — e.g. dragging in tracks from a flaky network / NAS share — now surface a friendly, copyable **“Oops” dialog** instead of taking the app down. Copy the details and send them over so we can fix the root cause.

---
*Per-user installer, no admin required. Upgrades 0.3.0 in place.*
