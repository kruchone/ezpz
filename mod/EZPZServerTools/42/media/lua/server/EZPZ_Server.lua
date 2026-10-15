-- EZPZ Server Tools, server half.
-- Collects the players' reports and writes them to Zomboid\Lua\ezpz_players.json (with ezpz's -cachedir
-- that's <data folder>\Lua\ezpz_players.json), where ezpz reads them. Writes only that one file.

if not isServer() then return end

EZPZ = EZPZ or {}
EZPZ.WRITE_MS = 5000
EZPZ.STALE_MS = 60000
EZPZ.reports = {}
EZPZ.lastWrite = 0

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

local function onClientCommand(module, command, player, args)
    if module ~= "EZPZ" or command ~= "report" or not player then return end
    local r = plain(args or {})
    r.username = player:getUsername()   -- from the connection, not from the report
    r.at = getTimestampMs()
    EZPZ.reports[r.username] = r
end

local function write()
    local now = getTimestampMs()
    local online = {}
    local list = getOnlinePlayers()
    if list then
        for i = 0, list:size() - 1 do online[list:get(i):getUsername()] = true end
    end
    local players = {}
    for name, r in pairs(EZPZ.reports) do
        if online[name] and now - r.at < EZPZ.STALE_MS then players[#players + 1] = r
        elseif not online[name] then EZPZ.reports[name] = nil end
    end
    local w = getFileWriter("ezpz_players.json", true, false)
    if not w then return end
    w:write(json({ mod = "0.1.0", written = now, online = list and list:size() or 0, players = players }))
    w:close()
end

local function onTick()
    local now = getTimestampMs()
    if now - EZPZ.lastWrite < EZPZ.WRITE_MS then return end
    EZPZ.lastWrite = now
    local ok, err = pcall(write)
    if not ok then print("[EZPZ] couldn't write ezpz_players.json: " .. tostring(err)) end
end

Events.OnClientCommand.Add(onClientCommand)
Events.OnTick.Add(onTick)
print("[EZPZ] EZPZ Server Tools 0.1.0 loaded on the server")
