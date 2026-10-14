<p align="center">
  <img src="assets/ezpz-256.png" width="96" alt="ezpz icon">
</p>

<h1 align="center">ezpz</h1>

<p align="center">
  <b>Set up, run, manage and reset a Project Zomboid Build 42 dedicated server on Windows, all from one small app.</b>
</p>

<p align="center">
  <img src="docs/screenshots/players.png" width="820" alt="ezpz Players & Admin page">
</p>

ezpz is a single `.exe` with no installer and no dependencies. It walks you through installing the dedicated server with SteamCMD, configuring it and opening the firewall, then gives you a console, player management, weather controls, backups and a safe **reset** button.

## Features

- **Guided setup.** A checklist turns green as you go. It covers installing or updating the server through Valve's SteamCMD (anonymous login, `public` or `unstable` branch), creating the default config, adding the Windows Firewall rule, and showing your local and public IP.
- **Configure.** Plain fields for the common settings (name, join password, max players, PvP, whitelist, ports, mods and so on), plus a searchable grid of every `.ini` setting. You can also import `.ini` and SandboxVars files made with the in-game Host menu.
- **Console.** The live server console with command history, a clean stop that saves the world, auto-restart after a crash, and scheduled restarts that warn players in-game at 15, 5 and 1 minutes, with an optional update during the restart.
- **Players & Admin.** Everyone who has ever joined, with online status, role, last seen, Steam ID and IP. One click sets a role (admin, moderator, gm, observer, priority, user). Also: kick, ban (optionally including the IP), mute voice, reset a password, god mode, invisibility, no-clip, teleport, give items, XP or vehicles, spawn hordes, lightning, and broadcast messages.
- **World & Weather.** Shows the world's Emergency Broadcast (EBS) radio frequency. Starts rain (with intensity), storms (with length), or clears the weather. Triggers lightning, thunder, helicopter and gunshot events.
- **Backups.** One-click `.zip` backups of the world, accounts and config, with restore. Automatic backups on start, before a restore and before a reset.
- **Reset.** Presets from "new world, keep everything else" up to "factory reset". Before anything is deleted you see exactly what will go and how big it is. A backup is made first, and you type the server ID to confirm.

| | |
|---|---|
| ![Setup](docs/screenshots/setup.png) | ![World & Weather](docs/screenshots/world.png) |
| ![Console](docs/screenshots/console.png) | ![Reset](docs/screenshots/reset.png) |

## Getting started

1. Download `ezpz.exe` from the [latest release](../../releases/latest) and put it anywhere.
2. Run it. You do **not** need to run it as administrator. Only the firewall step asks for permission, through the normal Windows prompt.
   Windows SmartScreen may warn you because the app isn't code-signed. Click **More info → Run anyway**, or build it yourself from source (see below).
3. Follow the **Setup** page from top to bottom: choose folders, server name and admin password, then **Install / Update server**, **Generate default config** and **Add Windows Firewall rule**.
4. Set a join password and anything else on **Configure**, then press **Start**.

For players outside your home network, forward **UDP 16261-16262** on your router to the PC running the server. Friends join with your public IP and port 16261.

Requirements: Windows 10 or 11 (64-bit), which already includes .NET Framework 4.8, plus enough RAM for the server. 8 GB for the server is a good start for a few friends.

## Resetting

| Preset | Deletes | Keeps |
|---|---|---|
| **New world** (recommended) | Map, loot, zombies, vehicles, characters | Settings, mods, accounts, admins, whitelist, bans |
| **Fresh map, keep characters** *(experimental)* | Everything in the world save except `players.db` | Characters (skills, inventory), accounts, settings |
| **New world + wipe accounts** | World, plus the accounts database (whitelist, admins, bans) | Settings and mods |
| **Factory reset** | Everything for this server: world, accounts, config, logs | The server install and your backups |

Only paths inside the data folder that belong to the current server ID are ever deleted. The server install, your backups and your own single-player saves (`%USERPROFILE%\Zomboid`) are never touched.

## Where things live

| What | Default location |
|---|---|
| Server install | `C:\PZServer\server` |
| SteamCMD | `C:\PZServer\steamcmd` |
| Server data (saves, config, accounts, logs), passed to the server as `-cachedir` | `C:\PZServer\data` |
| Backups | `C:\PZServer\backups` |
| ezpz settings | `%APPDATA%\ezpz\manager.ini` (the admin password is encrypted with Windows DPAPI) |

All of these can be changed on the Setup page.

## How it works

- **Launching.** ezpz reads the server's own `StartServer64.bat` and runs its `java.exe` line directly, swapping in your memory settings and adding `-servername`, `-cachedir` and `-adminpassword`. Because ezpz owns the server's console, it can send commands and stop the server cleanly with `quit`.
- **A server ezpz didn't start.** If ezpz finds such a server already running, it follows `server-console.txt` and sends commands over RCON. When you save on the Configure page, ezpz sets an RCON password if none exists.
- **Player list.** Built from the server's `*_connections.txt` logs, which record role, Steam ID and IP, plus the `players` command for who is online now.
- **EBS frequency.** Read from the server's startup log, where the game prints the channel it picked for the world.
- **Weather.** The server doesn't report its live weather to outside tools. ezpz shows the last change you made, and the in-game forecast is on the EBS.

## Building from source

No SDK needed. Windows includes the C# compiler.

```bat
build.bat
```

This produces `ezpz.exe`. To rebuild while a copy is running, pass another name: `build.bat ezpz-new.exe`.

The source is plain C# 5 / WinForms in `src\`. For development, `ezpz.exe --page N` opens on page N, and setting the `EZPZ_HOME` environment variable makes ezpz use a different settings folder, which is handy for testing against a throwaway server.

## Disclaimer

ezpz is an unofficial community tool. It is not affiliated with or endorsed by The Indie Stone. Project Zomboid is a trademark of The Indie Stone. Use at your own risk, and keep backups.

## License

[MIT](LICENSE)
