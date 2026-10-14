# Changelog

## Unreleased

- New Health page: Java memory use over time with a verdict and suggested max, pauses waiting for memory, crash and out-of-memory detection, PC memory and server CPU, and live tiles from PZ's `stats` command (players, server FPS, zombies, network, today's kills and deaths). ezpz now starts the server with a Java memory log for this.
- New Mods page: paste Workshop links, item numbers or collections. ezpz downloads them with SteamCMD, reads their mod IDs, and writes `WorkshopItems=`, `Mods=` and `Map=` in the Build 42 format, with checks for missing requirements and non-B42 mods.
- The console collapses the harmless stack traces B42 prints for mods without animation folders into one grey line.
- World & Weather no longer shows a weather status; the server doesn't report live weather.
- The Configure page no longer has the raw Mods, WorkshopItems and Map fields. Use the Mods page.

## 1.0.0

First public release.

- Guided setup: SteamCMD install and update (`public` or `unstable` branch), default config generation, Windows Firewall rule, local and public IP.
- Configure page with common settings, a full `.ini` grid, and import from the in-game Host menu.
- Live console, clean stop, auto-restart after crashes, scheduled restarts with in-game warnings.
- Players & Admin: player history from the server logs, live online status, B42 roles, moderation, admin powers, teleport, items, XP, vehicles and events.
- World & Weather: EBS frequency, rain, storms, lightning, thunder, helicopter and gunshot events.
- Backups with restore, and safe reset presets with a preview and a typed confirmation.
