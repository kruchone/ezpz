-- EZPZ Server Tools, server half.
-- Collects the players' reports and writes them to Zomboid\Lua\ezpz_players.json (with ezpz's -cachedir
-- that's <data folder>\Lua\ezpz_players.json), where ezpz reads them. Deaths are appended to
-- ezpz_events.json, one JSON object per line. It reads ezpz_season.txt, which ezpz writes (countdowns, the
-- season ladder, reward notices), and sends each player what their screen needs. Writes only those two files.

if not isServer() then return end

EZPZ = EZPZ or {}
EZPZ.VERSION = "0.3.0"
EZPZ.WRITE_MS = 5000
EZPZ.STALE_MS = 60000
EZPZ.SEASON_MS = 5000
EZPZ.SEASON_STALE_MS = 150000   -- ezpz rewrites the file at least once a minute; older means ezpz isn't running
EZPZ.reports = {}
EZPZ.lastWrite = 0
EZPZ.lastSeason = 0

-- minimal JSON writer: numbers, strings, booleans and nested tables (string keys = object, 1..n = array)
local function esc(s)
    s = tostring(s)
    s = s:gsub('\\', '\\\\'):gsub('"', '\\"'):gsub('\n', '\\n'):gsub('\r', '\\r'):gsub('\t', '\\t')
    return '"' .. s .. '"'
end

local function json(v)
    local t = type(v)
    if t == "number" then
        if v ~= v or v == math.huge or v == -math.huge then return "null" end
        return tostring(v)
    elseif t == "boolean" then return v and "true" or "false"
    elseif t == "string" then return esc(v)
    elseif t == "table" then
        if #v > 0 then
            local out = {}
            for i = 1, #v do out[#out + 1] = json(v[i]) end
            return "[" .. table.concat(out, ",") .. "]"
        end
        local out = {}
        for k, x in pairs(v) do out[#out + 1] = esc(k) .. ":" .. json(x) end
        return "{" .. table.concat(out, ",") .. "}"
    end
    return "null"
end

-- copies the report into a plain Lua table (args arrive as a KahluaTable)
local function plain(v)
    if type(v) ~= "table" then return v end
    local t = {}
    for k, x in pairs(v) do t[k] = plain(x) end
    return t
end

local function onlinePlayers()
    local byName = {}
    local list = getOnlinePlayers()
    if list then
        for i = 0, list:size() - 1 do
            local p = list:get(i)
            byName[p:getUsername()] = p
        end
    end
    return byName, list and list:size() or 0
end

-- ---------------------------------------------------------------- reports and deaths from the players

local function onDeath(player, args)
    local killer = args.killer
    -- only count a killer who is a real, online player other than the victim
    if killer then
        local online = onlinePlayers()
        if not online[killer] or killer == player:getUsername() then killer = nil end
    end
    local e = {
        at = getTimestampMs(), event = "death", victim = player:getUsername(), killer = killer,
        cause = args.cause, x = args.x, y = args.y, hours = args.hours, kills = args.kills,
    }
    local w = getFileWriter("ezpz_events.json", true, true)   -- append
    if not w then return end
    w:write(json(e) .. "\n")
    w:close()
end

local function onClientCommand(module, command, player, args)
    if module ~= "EZPZ" or not player then return end
    if command == "report" then
        local r = plain(args or {})
        r.username = player:getUsername()   -- from the connection, not from the report
        r.at = getTimestampMs()
        EZPZ.reports[r.username] = r
    elseif command == "death" then
        local ok, err = pcall(onDeath, player, plain(args or {}))
        if not ok then print("[EZPZ] couldn't record a death: " .. tostring(err)) end
    end
end

local function write()
    local now = getTimestampMs()
    local online, count = onlinePlayers()
    local players = {}
    for name, r in pairs(EZPZ.reports) do
        if online[name] and now - r.at < EZPZ.STALE_MS then players[#players + 1] = r
        elseif not online[name] then EZPZ.reports[name] = nil end
    end
    local w = getFileWriter("ezpz_players.json", true, false)
    if not w then return end
    w:write(json({ mod = EZPZ.VERSION, written = now, online = count, players = players }))
    w:close()
end

-- ---------------------------------------------------------------- the season file from ezpz

local function split(s, sep)
    local out, from = {}, 1
    while true do
        local i = string.find(s, sep, from, true)
        if not i then out[#out + 1] = string.sub(s, from); return out end
        out[#out + 1] = string.sub(s, from, i - 1)
        from = i + 1
    end
end

-- key=value lines, "p|name|rank|score|7 categories" rows and "notice|player|item|title" rows
local function readSeasonFile()
    local r = getFileReader("ezpz_season.txt", false)
    if not r then return nil end
    local d = { rows = {}, byName = {}, notices = {} }
    while true do
        local line = r:readLine()
        if not line then break end
        if string.sub(line, 1, 2) == "p|" then
            local f = split(line, "|")
            local row = { name = f[2], rank = tonumber(f[3]) or 0, score = f[4] or "", cats = {} }
            for i = 5, #f do row.cats[#row.cats + 1] = f[i] end
            d.rows[#d.rows + 1] = row
            d.byName[row.name] = row
        elseif string.sub(line, 1, 7) == "notice|" then
            local f = split(line, "|")
            d.notices[#d.notices + 1] = { player = f[2], item = f[3], title = f[4] }
        else
            local i = string.find(line, "=", 1, true)
            if i then d[string.sub(line, 1, i - 1)] = string.sub(line, i + 1) end
        end
    end
    r:close()
    return d
end

-- seconds from now (0 once it's passed), or nil when nothing is scheduled
local function secondsUntil(ms, now)
    local v = tonumber(ms) or 0
    if v <= 0 then return nil end
    return math.max(0, math.floor((v - now) / 1000))
end

-- What one player's screen needs. Times are sent as seconds from now, so players' clocks don't matter.
local function stateFor(d, name, now)
    local s = { v = 1 }
    if not d or now - (tonumber(d.written) or 0) > EZPZ.SEASON_STALE_MS then
        s.off = true
        return s
    end
    s.restart = secondsUntil(d.restartAt, now)
    s.wipe = secondsUntil(d.endAt, now)
    s.season = tonumber(d.season) or 0
    if s.season > 0 then
        s.endNear = secondsUntil(d.endNearAt, now)
        local start = tonumber(d.seasonStart) or 0
        s.day = start > 0 and math.floor((now - start) / 86400000) + 1 or 0
        s.ladder = d.ladder == "1"
        if d.extract then   -- last helicopter out: "x,y,radius"
            local f = split(d.extract, ",")
            if tonumber(f[1]) and tonumber(f[2]) then s.extract = { x = tonumber(f[1]), y = tonumber(f[2]), r = tonumber(f[3]) or 50 } end
        end
        if s.ladder then
            local top = {}
            for i = 1, math.min(5, #d.rows) do
                local row = d.rows[i]
                top[#top + 1] = { name = row.name, rank = row.rank, score = row.score }
            end
            s.top = top
            s.ranked = #d.rows
            local me = d.byName[name]
            if me then s.me = { rank = me.rank, score = me.score, cats = me.cats } end
        end
    end
    for _, n in ipairs(d.notices) do
        if n.player == name then s.notice = { item = n.item, title = n.title } end
    end
    return s
end

local function sendSeason()
    local now = getTimestampMs()
    local d = readSeasonFile()
    local online = onlinePlayers()
    for name, p in pairs(online) do
        sendServerCommand(p, "EZPZ", "state", stateFor(d, name, now))
    end
end

local function onTick()
    local now = getTimestampMs()
    if now - EZPZ.lastWrite >= EZPZ.WRITE_MS then
        EZPZ.lastWrite = now
        local ok, err = pcall(write)
        if not ok then print("[EZPZ] couldn't write ezpz_players.json: " .. tostring(err)) end
    end
    if now - EZPZ.lastSeason >= EZPZ.SEASON_MS then
        EZPZ.lastSeason = now
        local ok, err = pcall(sendSeason)
        if not ok then print("[EZPZ] couldn't send the season state: " .. tostring(err)) end
    end
end

Events.OnClientCommand.Add(onClientCommand)
Events.OnTick.Add(onTick)
print("[EZPZ] EZPZ Server Tools " .. EZPZ.VERSION .. " loaded on the server")
