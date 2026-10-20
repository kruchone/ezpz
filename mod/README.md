# EZPZ Server Tools (Project Zomboid mod)

A small Build 42 mod that pairs with ezpz. It adds no items and changes nothing in the game.

- **For the dashboard:** each player's game reports their character's health, injuries, moodles, stats and position to
  the server every 10 seconds. The server writes the online players to `<data folder>\Lua\ezpz_players.json`, which
  ezpz reads. Deaths (with the killer, when it was another player) are appended to `Lua\ezpz_events.json`, one JSON
  object per line, for PvP on the season ladder.
- **For players:** ezpz writes `Lua\ezpz_season.txt` (countdowns, the season ladder, reward notices). The server half
  reads it every 5 seconds and sends each player what their screen needs: a bar above the hotbar with the restart and wipe
  countdowns (red during THE END IS NEAR), a one-time THE END IS NEAR banner, the season panel (click the bar), and a
  reward notice.
- **For admins:** an **EZPZ Server Tools** button in the game's Admin panel opens a preview pane that shows each panel with sample data, on
  that admin's screen only.

```
EZPZServerTools/
  42/mod.info
  42/media/lua/client/EZPZ_Client.lua   reports the local player's state and death (sendClientCommand "EZPZ")
  42/media/lua/client/EZPZ_UI.lua       the on-screen panels and the admin preview pane
  42/media/lua/server/EZPZ_Server.lua   writes ezpz_players.json and ezpz_events.json, reads ezpz_season.txt,
                                        sends each player their state (sendServerCommand "EZPZ" / "state")
  42/poster.png                         the picture in the game's mod list
  common/                               required by Build 42
```

## Testing without the Workshop

1. Copy `EZPZServerTools` into the server's mods folder: `<data folder>\mods\` (ezpz runs the server with
   `-cachedir=<data folder>`).
2. Copy it into your own game's mods folder too: `%USERPROFILE%\Zomboid\mods\`. Players need the mod locally to
   join; once it's on the Workshop their game downloads it.
3. Add `\EZPZServerTools` to the server's `Mods=` line and restart the server.
4. Join, wait 10-15 seconds, and look for `<data folder>\Lua\ezpz_players.json`.
5. As an admin, open the Admin panel, click **EZPZ Server Tools** and turn on each preview to check the panels.

Data reported by a player's game could be faked by a cheater, so use it for information, not for automatic action.

## Packaging

`.\mod\package.ps1` builds `dist\EZPZServerTools-<version>.zip`, which holds the mod, `install.cmd` (copies it into
`%USERPROFILE%\Zomboid\mods`, or into `<data folder>\mods` when given a data folder) and a README for players and
server admins. With `-Workshop` it also stages `%USERPROFILE%\Zomboid\Workshop\EZPZServerTools` for the game's
Workshop uploader (main menu > Workshop). The script never uploads anything.
