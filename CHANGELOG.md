# Changelog

## Unreleased

- Discord commands through ezpz's own connection, using the chat bridge's bot: `/status` for everyone; `/start`, `/restart` (with an in-game warning, 5 minutes by default) and `/backup` for admins (Discord administrators, or a role you name). The new **Invite bot** button on the Discord page builds the invite link from the bot token (scopes `bot` and `applications.commands`; View Channels, Send Messages, Read Message History and Embed Links), opens it and copies it.
- Whitelist requests from Discord: players use `/whitelist <username>`. Admins approve or deny with buttons in a requests channel, or on the Players page (a "Whitelist requests" button appears when there are any). Approving creates the account with a random password and the bot sends it to the player privately; if their DMs are closed, the approver gets it to pass on.
- Discord notifications can now include player deaths, survival milestones (7, 14, 30, 60, 100, 180 and 365 days), a daily summary at a time you pick, and a live status message that edits itself (online, players, uptime, next restart). When ezpz closes, the status message says it's no longer updating. With the live status message on, the separate online, stopped and join/leave posts are skipped, since the status message shows those. All are off until you tick them.
- ezpz keeps its own log for bug reports: `%APPDATA%\ezpz\logs\ezpz-<date>.log`, one file per day, kept for 14 days. It records what ezpz did, the commands it sent, startup problems and every unexpected error with its full details. Passwords and Discord webhook links are never written. Open it with the new **ezpz log** button on the Console page, and the "Unexpected error" box says where it is. It replaces `startup.log`.
- Fixed: switching servers while on the Logs page could pop up "Index was out of range" errors.
- Players: the give-item and spawn-vehicle boxes now suggest as you type. Type part of a name or ID ("fire axe", "ambulance") and pick from the list. It's read from the installed server's own script files and the enabled mods (with the mod's name shown), so it always matches your server's build. The chosen item's name shows under the box, and ezpz asks before sending an ID the server doesn't know.
- Players: the action panel no longer needs a sideways scroll bar, and the player list's IP column fits the space left. Scrolled to the bottom, the panel's last buttons (Lightning strike, Thunder) are no longer cut off. The buttons along the bottom wrap instead of being cut off on narrow windows, and the character sheet shrinks on short windows so the player list stays visible.
- Logs: double-click a line with a location, or right-click it and choose View this spot on pzmap.org, to open that spot on the community map in your browser. Admin lines with decimal coordinates (such as spawned hordes) now show their location too.

## 1.1.0

- Backups: choose ezpz, Project Zomboid's built-in backups, or both, and set each side's options in one place (PZ's are written to the server config). ezpz can also back up every N hours while the server runs. Both kinds appear in one list with what each contains and the world version, and either kind can be restored. A restore now only replaces what the backup contains, so a config-only backup leaves the world alone. The Console page's back-up-on-start option moved to the Backups page.
- New Logs page: a searchable, filterable timeline of chat, admin actions, console commands, PvP, player actions, joins and skill level-ups from PZ's logs (current and archived runs), with View logs from the Players page. ezpz's own background checks and PZ's internal log noise are left out, and passwords in setpassword commands are hidden.
- Splash screen at startup. ezpz preloads every page behind it (players, character sheets, mods, the Sandbox editor, server config, Discord, radio, memory history, backups, firewall), so pages open instantly. The Sandbox page went from a 2.5 s freeze on first open to about 50 ms. Anything that fails to load is listed in plain words, with Continue, Copy details and Quit, and saved to `startup.log`. The server is never started by this.
- Character sheets on the Players page: days survived, deaths, last position, skills by group with levels, and recent level-ups (from PZ's PerkLog), plus per-player network traffic from the metrics server while they're online.
- Far less noise in the server's logs: Home's live tiles now read Project Zomboid's built-in metrics server, every 5 seconds and with nothing logged, instead of sending `stats` commands every 10 seconds. Who is online, including Discord join and leave alerts, comes from the server's connection log instead of polling `players`.
- The firewall step also blocks inbound TCP to the server's Java from other machines, keeping the metrics and RCON ports private. Players only use UDP.
- Home flags deadlocked server threads, a sign the server has frozen.
- Fixed: a quick restart could refuse to start, because the previous server process was still closing. ezpz now waits for it.
- Fixed: leaving the Discord or Configure page silently dropped unsaved edits. ezpz now asks to save, discard or stay, also when switching servers or closing. The Discord page has a single Save for the whole page.
- New Discord page: guided setup of the game's built-in chat bridge bot, with a setup check (token, server, channel names, Message Content Intent), plus ezpz notifications through a webhook (online, stopped, crashes, restart warnings, updates, players joining and leaving, memory problems).
- New Sandbox page: a form for every world rule, built from the server's own SandboxVars file, with search, a changed-from-default filter and reset-to-default. It edits values in place, keeps a .bak copy, and falls back to Notepad if the file looks unfamiliar.
- Mod update watcher: while the server runs, ezpz checks the Workshop for mod updates and can restart after a 5-minute in-game warning, or when nobody is online. Home and the Mods page show mod update status.
- Fixed: opening the Mods page could mark it as changed and move ticked mods to the end of the load order.
- Several servers: a switcher under the server name (top left) lists your servers and adds new ones. Each has its own settings and data folder and can share an install or use its own. One runs at a time, and ezpz checks that no other PZ server is running before starting. Existing settings become the first server automatically.
- New Home page, opened first: server status and quick actions, an at-a-glance row (EBS, mods, last backup, next restart, server build), a server update check with one-click update, and the health section. The separate Health page is gone.
- The window title now reads `<Server ID>: <status> — ezpz <version>`.
- Memory health groups simultaneous allocation stalls into one pause and treats startup pauses as normal. One or two brief pauses are a warning, not "too small".
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
