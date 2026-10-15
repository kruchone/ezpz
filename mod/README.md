# EZPZ Server Tools (Project Zomboid mod)

A small Build 42 mod that pairs with ezpz. Each player's game reports their character's health, injuries, moodles,
stats and position to the server every 10 seconds; the server writes the online players to
`<data folder>\Lua\ezpz_players.json`, which ezpz reads for its dashboard. It adds no items and changes nothing in the
game.

```
EZPZServerTools/
  42/mod.info
  42/media/lua/client/EZPZ_Client.lua   reports the local player's state (sendClientCommand "EZPZ" / "report")
  42/media/lua/server/EZPZ_Server.lua   collects reports, writes ezpz_players.json every 5 s
  common/                               required by Build 42
```

## Testing without the Workshop

1. Copy `EZPZServerTools` into the server's mods folder: `<data folder>\mods\` (ezpz runs the server with
   `-cachedir=<data folder>`).
2. Copy it into your own game's mods folder too: `%USERPROFILE%\Zomboid\mods\`. Players need the mod locally to
   join; once it's on the Workshop their game downloads it.
3. Add `\EZPZServerTools` to the server's `Mods=` line and restart the server.
4. Join, wait 10-15 seconds, and look for `<data folder>\Lua\ezpz_players.json`.

Data reported by a player's game could be faked by a cheater, so use it for information, not for automatic action.
