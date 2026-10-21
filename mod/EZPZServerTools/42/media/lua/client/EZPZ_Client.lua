-- EZPZ Server Tools, client half.
-- Every few seconds, the player's own game sends a small summary of their character to the server
-- (in multiplayer the player's game is the authority on their body and moodles), and reports the
-- player's death (and who killed them, if it was another player). Read-only: nothing about the game is
-- changed. The on-screen panels are in EZPZ_UI.lua.

EZPZ = EZPZ or {}
EZPZ.REPORT_MS = 10000
EZPZ.lastReport = 0

local MOODLES = {
    "HUNGRY", "THIRST", "TIRED", "ENDURANCE", "PANIC", "STRESS", "UNHAPPY", "BORED", "PAIN", "SICK",
    "DRUNK", "INJURED", "BLEEDING", "HAS_A_COLD", "HYPOTHERMIA", "HYPERTHERMIA", "WET", "WINDCHILL",
    "HEAVY_LOAD", "ZOMBIE", "ANGRY", "UNCOMFORTABLE", "NOXIOUS_SMELL", "CANT_SPRINT",
}

local STATS = {
    "HUNGER", "THIRST", "FATIGUE", "ENDURANCE", "PANIC", "STRESS", "UNHAPPINESS", "BOREDOM", "PAIN",
    "SICKNESS", "INTOXICATION", "TEMPERATURE", "WETNESS", "ZOMBIE_INFECTION",
}

-- Calls fn and returns its result, or nil if this game version doesn't have it.
local function try(fn)
    local ok, v = pcall(fn)
    if ok then return v end
    return nil
end

local function collect(p)
    local d = {}
    local bd = p:getBodyDamage()
    d.health = try(function() return bd:getOverallBodyHealth() end)
    d.infected = try(function() return bd:IsInfected() end)

    -- injuries, counted over the body parts
    local bitten, scratched, bleeding, deep, fractures, burns = 0, 0, 0, 0, 0, 0
    local parts = try(function() return bd:getBodyParts() end)
    if parts then
        for i = 0, parts:size() - 1 do
            local bp = parts:get(i)
            if try(function() return bp:bitten() end) then bitten = bitten + 1 end
            if try(function() return bp:scratched() end) then scratched = scratched + 1 end
            if try(function() return bp:bleeding() end) then bleeding = bleeding + 1 end
            if try(function() return bp:deepWounded() end) then deep = deep + 1 end
            if (try(function() return bp:getFractureTime() end) or 0) > 0 then fractures = fractures + 1 end
            if (try(function() return bp:getBurnTime() end) or 0) > 0 then burns = burns + 1 end
        end
    end
    d.bitten, d.scratched, d.bleeding, d.deep, d.fractures, d.burns = bitten, scratched, bleeding, deep, fractures, burns

    -- moodles the player currently has, with their level (1-4)
    local moodles = {}
    local md = p:getMoodles()
    for _, name in ipairs(MOODLES) do
        local mt = try(function() return MoodleType[name] end)
        if mt then
            local lvl = try(function() return md:getMoodleLevel(mt) end)
            if lvl and lvl > 0 then moodles[name] = lvl end
        end
    end
    d.moodles = moodles

    -- raw stats (0-1 for most)
    local stats = {}
    local st = p:getStats()
    for _, name in ipairs(STATS) do
        local cs = try(function() return CharacterStat[name] end)
        if cs then
            local v = try(function() return st:get(cs) end)
            if v then stats[name] = v end
        end
    end
    d.stats = stats

    d.x, d.y, d.z = p:getX(), p:getY(), p:getZ()
    d.kills = try(function() return p:getZombieKills() end)
    d.hours = try(function() return p:getHoursSurvived() end)
    local v = try(function() return p:getVehicle() end)
    if v then
        d.vehicle = try(function() return v:getScriptName() end) or "vehicle"
        d.driving = try(function() return v:getDriver() == p end)
    end
    d.asleep = try(function() return p:isAsleep() end)
    d.mod = "0.3.0"
    return d
end

local function onTick()
    if not isClient() then return end
    local now = getTimestampMs()
    if now - EZPZ.lastReport < EZPZ.REPORT_MS then return end
    EZPZ.lastReport = now
    local p = getPlayer()
    if not p or p:isDead() then return end
    local ok, data = pcall(collect, p)
    if ok and data then sendClientCommand(p, "EZPZ", "report", data) end
end

-- The player's own death: the cause, and the killer if it was another player (for PvP on the ezpz ladder).
local function onPlayerDeath(p)
    if not isClient() or p ~= getPlayer() then return end
    local d = { cause = "other" }
    local by = try(function() return p:getAttackedBy() end)
    if by and instanceof(by, "IsoPlayer") and by ~= p then
        d.cause = "player"
        d.killer = try(function() return by:getUsername() end)
    elseif by and instanceof(by, "IsoZombie") then
        d.cause = "zombie"
    end
    d.x, d.y = p:getX(), p:getY()
    d.hours = try(function() return p:getHoursSurvived() end)
    d.kills = try(function() return p:getZombieKills() end)
    sendClientCommand(p, "EZPZ", "death", d)
end

Events.OnTick.Add(onTick)
Events.OnPlayerDeath.Add(onPlayerDeath)
