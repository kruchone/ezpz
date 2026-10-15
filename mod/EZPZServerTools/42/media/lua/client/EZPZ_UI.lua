-- EZPZ Server Tools, on-screen panels.
-- The server half sends each player their state every few seconds (see EZPZ_Server.lua): countdowns to the
-- next restart and wipe, the season ladder, THE END IS NEAR, and reward notices. This file draws it:
--   * a bar just above the hotbar with the countdowns (red during THE END IS NEAR); click it for the season panel
--   * a one-time THE END IS NEAR banner, above the bar
--   * the season panel (above the bar): your rank and categories, and the top 5
--   * a reward notice when a reward from last season arrives
-- Admins and moderators also get an "EZPZ Server Tools" button in the game's Admin panel. It opens a preview
-- pane: each panel can be shown with sample data to check how it looks. Previews are local to that admin's screen.

require "ISUI/ISPanel"
require "ISUI/ISButton"
require "ISUI/AdminPanel/ISAdminPanelUI"

EZPZ = EZPZ or {}
EZPZ.VERSION = EZPZ.VERSION or "0.2.0"
EZPZ.state = nil          -- the latest state from the server
EZPZ.stateAt = 0          -- when it arrived (local ms)
EZPZ.preview = { restart = false, wipe = false, endnear = false, season = false, reward = false }
EZPZ.seasonOpen = false
EZPZ.bannerShownFor = nil -- the season the banner was shown for (once per session)
EZPZ.noticesSeen = {}
EZPZ.ui = {}

local CATEGORY_NAMES = { "Endurance", "Skills", "Slayer", "Explorer", "Resilience", "Final Stand", "PvP" }

local function try(fn)
    local ok, v = pcall(fn)
    if ok then return v end
    return nil
end

local function fontH(font) return getTextManager():getFontHeight(font) end
local function textW(font, s) return getTextManager():MeasureStringX(font, s) end

local PAD = 12                                     -- space around and between things
local function btnH() return fontH(UIFont.Small) + 8 end

local function pad2(n) n = math.floor(n); if n < 10 then return "0" .. n end return tostring(n) end

-- "3d 4h", "5:02:09", "12:34"
local function clock(secs)
    if not secs or secs < 0 then return "" end
    secs = math.floor(secs)
    if secs >= 2 * 86400 then return math.floor(secs / 86400) .. "d " .. math.floor((secs % 86400) / 3600) .. "h" end
    local h = math.floor(secs / 3600)
    local m = math.floor((secs % 3600) / 60)
    local s = secs % 60
    if h > 0 then return h .. ":" .. pad2(m) .. ":" .. pad2(s) end
    return m .. ":" .. pad2(s)
end

local function anyPreview()
    for _, v in pairs(EZPZ.preview) do if v then return true end end
    return false
end

-- Sample data for the preview pane, using the admin's own name.
local function sampleState()
    local me = try(function() return getPlayer():getUsername() end) or "you"
    local pv = EZPZ.preview
    local s = { v = 1, season = 0 }
    if pv.restart then s.restart = 12 * 60 + 34 end
    if pv.wipe then s.wipe = 3 * 86400 + 4 * 3600 end
    if pv.endnear then s.wipe = 5 * 3600 + 17 * 60; s.endNear = 0 end
    if pv.season or pv.endnear then
        s.season = 3
        s.day = 12
        s.wipe = s.wipe or (3 * 86400 + 4 * 3600)
        s.endNear = s.endNear or (2 * 86400)
        s.ladder = true
        s.ranked = 14
        s.top = {
            { name = "Rick", rank = 1, score = "84.5" }, { name = me, rank = 2, score = "71.0" },
            { name = "Daryl", rank = 3, score = "66.2" }, { name = "Michonne", rank = 4, score = "59.8" },
            { name = "Carol", rank = 5, score = "52.1" },
        }
        s.me = { rank = 2, score = "71.0", cats = { "6d 2h", "+38", "1,204", "212 areas", "0.4 /10h", "alive", "0" } }
    end
    if pv.reward then s.notice = { item = "Gold Sports Medal", title = "The Survivor of Season 2" } end
    s.preview = true
    return s
end

-- The state to show right now, with the countdowns run down since it arrived.
local function current()
    if anyPreview() then return sampleState(), 0 end
    local s = EZPZ.state
    if not s or s.off then return nil, 0 end
    return s, (getTimestampMs() - EZPZ.stateAt) / 1000
end

-- seconds left on a countdown, or -1 when there's none
local function left(v, elapsed)
    if v == nil then return -1 end
    return math.max(0, v - elapsed)
end

local function isNear(s, elapsed)
    return s.season ~= nil and s.season > 0 and s.endNear ~= nil and s.wipe ~= nil and left(s.endNear, elapsed) == 0
end

-- ================================================================== the top bar

EZPZBar = ISPanel:derive("EZPZBar")

function EZPZBar:new()
    local o = ISPanel:new(0, 0, 300, fontH(UIFont.Medium) + 12)
    setmetatable(o, self)
    self.__index = self
    o.background = true
    o.backgroundColor = { r = 0, g = 0, b = 0, a = 0.55 }
    o.borderColor = { r = 1, g = 1, b = 1, a = 0.12 }
    o.text = ""
    o.red = false
    o.clickable = false
    return o
end

function EZPZBar:prerender()
    if self.red then
        self.backgroundColor = { r = 0.45, g = 0.02, b = 0.02, a = 0.8 }
        self.borderColor = { r = 1, g = 0.2, b = 0.2, a = 0.7 }
    else
        self.backgroundColor = { r = 0, g = 0, b = 0, a = 0.55 }
        self.borderColor = { r = 1, g = 1, b = 1, a = 0.12 }
    end
    ISPanel.prerender(self)
end

function EZPZBar:render()
    local r, g, b = 0.92, 0.92, 0.92
    if self.red then r, g, b = 1, 0.85, 0.85 end
    self:drawTextCentre(self.text, self.width / 2, 6, r, g, b, 1, UIFont.Medium)
end

function EZPZBar:onMouseUp(x, y)
    if self.clickable then
        EZPZ.seasonOpen = not EZPZ.seasonOpen
        EZPZ.refresh()
    end
    return true
end

-- ================================================================== THE END IS NEAR banner

EZPZBanner = ISPanel:derive("EZPZBanner")

function EZPZBanner:new()
    local o = ISPanel:new(0, 0, 600, fontH(UIFont.Large) + fontH(UIFont.Medium) + 30)
    setmetatable(o, self)
    self.__index = self
    o.background = false
    o.shownAt = 0
    o.sub = ""
    return o
end

function EZPZBanner:render()
    local t = (getTimestampMs() - self.shownAt) / 1000
    local a = 1
    if t > 8 then a = math.max(0, 1 - (t - 8) / 2) end
    self:drawRect(0, 0, self.width, self.height, 0.6 * a, 0.2, 0, 0)
    self:drawTextCentre("THE END IS NEAR", self.width / 2, 8, 1, 0.15, 0.1, a, UIFont.Large)
    self:drawTextCentre(self.sub, self.width / 2, 14 + fontH(UIFont.Large), 1, 0.85, 0.85, a, UIFont.Medium)
end

function EZPZBanner:onMouseUp() self:setVisible(false); return true end

-- ================================================================== the season panel

EZPZSeason = ISPanel:derive("EZPZSeason")

function EZPZSeason:new()
    local o = ISPanel:new(0, 0, 330, 100)
    setmetatable(o, self)
    self.__index = self
    o.background = true
    o.backgroundColor = { r = 0.05, g = 0.05, b = 0.06, a = 0.85 }
    o.borderColor = { r = 1, g = 1, b = 1, a = 0.15 }
    o.lines = {}
    return o
end

-- lines: { text, right, color, font }
function EZPZSeason:render()
    local y = 8
    for _, l in ipairs(self.lines) do
        local font = l.font or UIFont.Small
        local c = l.color or { 0.9, 0.9, 0.9 }
        if l.text ~= "" then self:drawText(l.text, 12, y, c[1], c[2], c[3], 1, font) end
        if l.right then self:drawTextRight(l.right, self.width - 12, y, c[1], c[2], c[3], 1, font) end
        y = y + fontH(font) + (l.gap or 2)
    end
end

function EZPZSeason:onMouseUp() EZPZ.seasonOpen = false; EZPZ.refresh(); return true end

local GOLD = { 1, 0.82, 0.3 }
local MUTED = { 0.6, 0.62, 0.66 }
local RED = { 1, 0.35, 0.3 }

local function seasonLines(s, elapsed)
    local lines = {}
    local add = function(text, right, color, font, gap) lines[#lines + 1] = { text = text, right = right, color = color, font = font, gap = gap } end
    local wipe = left(s.wipe, elapsed)
    local near = isNear(s, elapsed)
    add("Season " .. s.season, s.day and s.day > 0 and ("day " .. s.day) or nil, GOLD, UIFont.Medium, 4)
    if wipe >= 0 then
        if near then add("THE END IS NEAR", clock(wipe), RED, nil, 8)
        else add("Ends with the wipe in", clock(wipe), MUTED, nil, 8) end
    end
    if not s.ladder then
        add("The ladder is secret until THE END IS NEAR.", nil, MUTED)
        add("Keep surviving.", nil, MUTED)
        return lines
    end
    if s.me then
        add("You:  #" .. s.me.rank .. (s.ranked and (" of " .. s.ranked) or ""), s.me.score, { 0.55, 0.85, 0.45 }, UIFont.Medium, 4)
        for i, name in ipairs(CATEGORY_NAMES) do
            local v = s.me.cats and s.me.cats[i]
            if v and v ~= "" and not (name == "PvP" and v == "0") then add("    " .. name, v, { 0.8, 0.8, 0.8 }) end
        end
        add("", nil, nil, nil, 4)
    else
        add("You're not ranked yet: play a few hours to qualify.", nil, MUTED, nil, 8)
    end
    add("Top 5", nil, GOLD, nil, 4)
    if not s.top or #s.top == 0 then add("    Nobody qualifies yet.", nil, MUTED) end
    for _, row in ipairs(s.top or {}) do
        add("    #" .. row.rank .. "  " .. row.name, row.score, row.rank == 1 and GOLD or { 0.85, 0.85, 0.85 })
    end
    add("", nil, nil, nil, 2)
    add("Click to close", nil, MUTED)
    return lines
end

-- ================================================================== reward notice

EZPZReward = ISPanel:derive("EZPZReward")

function EZPZReward:new()
    local o = ISPanel:new(0, 0, 440, PAD + fontH(UIFont.Medium) * 2 + fontH(UIFont.Small) + 8 + PAD + btnH() + PAD)
    setmetatable(o, self)
    self.__index = self
    o.background = true
    o.backgroundColor = { r = 0.08, g = 0.07, b = 0.03, a = 0.92 }
    o.borderColor = { r = 1, g = 0.82, b = 0.3, a = 0.8 }
    o.item = ""
    o.title = ""
    return o
end

function EZPZReward:createChildren()
    local w = textW(UIFont.Small, "OK") + 60
    self.ok = ISButton:new((self.width - w) / 2, self.height - PAD - btnH(), w, btnH(), "OK", self, EZPZReward.close)
    self.ok:initialise()
    self:addChild(self.ok)
end

function EZPZReward:render()
    local y = PAD
    self:drawTextCentre("A reward from last season", self.width / 2, y, 1, 0.82, 0.3, 1, UIFont.Medium)
    y = y + fontH(UIFont.Medium) + 6
    self:drawTextCentre(self.item, self.width / 2, y, 1, 1, 1, 1, UIFont.Medium)
    y = y + fontH(UIFont.Medium) + 2
    self:drawTextCentre(self.title, self.width / 2, y, 0.8, 0.8, 0.8, 1, UIFont.Small)
end

function EZPZReward:close()
    self:setVisible(false)
    if EZPZ.state and EZPZ.state.notice then
        EZPZ.noticesSeen[EZPZ.state.notice.item .. "|" .. EZPZ.state.notice.title] = true
    end
    if EZPZ.preview.reward then EZPZ.preview.reward = false; EZPZ.refreshAdmin() end
    EZPZ.refresh()
end

-- ================================================================== admin preview pane

EZPZAdmin = ISPanel:derive("EZPZAdmin")

local PREVIEWS = {
    { key = "restart", label = "Restart countdown" },
    { key = "wipe", label = "Wipe countdown" },
    { key = "endnear", label = "THE END IS NEAR (bar and banner)" },
    { key = "season", label = "Season panel" },
    { key = "reward", label = "Reward notice" },
}

local ADMIN_TITLE = "Previews show sample data, on your screen only."

function EZPZAdmin:new()
    local o = ISPanel:new(0, 0, 360, 200)
    setmetatable(o, self)
    self.__index = self
    o.background = true
    o.backgroundColor = { r = 0.06, g = 0.06, b = 0.07, a = 0.94 }
    o.borderColor = { r = 1, g = 1, b = 1, a = 0.25 }
    o.moveWithMouse = true
    o.buttons = {}
    return o
end

function EZPZAdmin:createChildren()
    -- as wide as the longest line needs
    local w = textW(UIFont.Medium, "EZPZ Server Tools " .. (EZPZ.VERSION or ""))
    w = math.max(w, textW(UIFont.Small, ADMIN_TITLE))
    for _, p in ipairs(PREVIEWS) do w = math.max(w, textW(UIFont.Small, "[off]   " .. p.label) + 40) end
    local hideW = textW(UIFont.Small, "Hide all previews") + 30
    local closeW = textW(UIFont.Small, "Close") + 40
    w = math.max(w, hideW + closeW + PAD)
    self:setWidth(w + PAD * 2)

    local y = PAD + fontH(UIFont.Medium) + 4 + fontH(UIFont.Small) + PAD
    for i, p in ipairs(PREVIEWS) do
        local b = ISButton:new(PAD, y, w, btnH(), "", self, EZPZAdmin.toggle)
        b.internal = p.key
        b:initialise()
        self:addChild(b)
        self.buttons[i] = b
        y = y + btnH() + 6
    end
    y = y + PAD
    local hide = ISButton:new(PAD, y, hideW, btnH(), "Hide all previews", self, EZPZAdmin.hideAll)
    hide:initialise()
    self:addChild(hide)
    local close = ISButton:new(self.width - PAD - closeW, y, closeW, btnH(), "Close", self, EZPZAdmin.close)
    close:initialise()
    self:addChild(close)
    self:setHeight(y + btnH() + PAD)
    self:update()
end

function EZPZAdmin:update()
    for i, p in ipairs(PREVIEWS) do
        local on = EZPZ.preview[p.key]
        self.buttons[i]:setTitle((on and "[on]    " or "[off]   ") .. p.label)
        self.buttons[i].textColor = on and { r = 0.55, g = 0.9, b = 0.45, a = 1 } or { r = 1, g = 1, b = 1, a = 1 }
    end
end

function EZPZAdmin:render()
    self:drawText("EZPZ Server Tools " .. (EZPZ.VERSION or ""), PAD, PAD, 1, 0.82, 0.3, 1, UIFont.Medium)
    self:drawText(ADMIN_TITLE, PAD, PAD + fontH(UIFont.Medium) + 4, 0.6, 0.62, 0.66, 1, UIFont.Small)
end

function EZPZAdmin:toggle(button)
    EZPZ.preview[button.internal] = not EZPZ.preview[button.internal]
    if button.internal == "endnear" and EZPZ.preview.endnear then EZPZ.showBanner("Season 3 ends in 5 hours (preview)") end
    if button.internal == "season" then EZPZ.seasonOpen = EZPZ.preview.season end
    self:update()
    EZPZ.refresh()
end

function EZPZAdmin:hideAll()
    for k, _ in pairs(EZPZ.preview) do EZPZ.preview[k] = false end
    EZPZ.seasonOpen = false
    if EZPZ.ui.banner then EZPZ.ui.banner:setVisible(false) end
    self:update()
    EZPZ.refresh()
end

function EZPZAdmin:close()
    self:hideAll()
    self:setVisible(false)
end

function EZPZ.refreshAdmin() if EZPZ.ui.admin then EZPZ.ui.admin:update() end end

-- ================================================================== putting it on screen

local function screenW() return getCore():getScreenWidth() end
local function screenH() return getCore():getScreenHeight() end

-- Just above the player's hotbar (or near the bottom of the screen if there isn't one).
local function barY(h)
    local hb = try(function() return getPlayerHotbar(0) end)
    local top = hb and try(function() return hb:getY() end)
    if not top or top <= 0 then top = screenH() - 60 end
    return top - h - 8
end

function EZPZ.showBanner(sub)
    local b = EZPZ.ui.banner
    if not b then return end
    b.sub = sub
    b.shownAt = getTimestampMs()
    b:setWidth(math.max(520, textW(UIFont.Large, "THE END IS NEAR") + 80, textW(UIFont.Medium, sub) + 60))
    b:setX((screenW() - b.width) / 2)
    -- above the bar (and the season panel, if it's open), so everything grows up from the hotbar
    local top = barY(EZPZ.ui.bar.height)
    if EZPZ.ui.season:isVisible() then top = EZPZ.ui.season:getY() end
    b:setY(top - b.height - 10)
    b:setVisible(true)
    b:bringToTop()
end

local function create()
    if EZPZ.ui.bar then return end
    local ui = EZPZ.ui
    ui.bar = EZPZBar:new(); ui.bar:initialise(); ui.bar:addToUIManager(); ui.bar:setVisible(false)
    ui.banner = EZPZBanner:new(); ui.banner:initialise(); ui.banner:addToUIManager(); ui.banner:setVisible(false)
    ui.season = EZPZSeason:new(); ui.season:initialise(); ui.season:addToUIManager(); ui.season:setVisible(false)
    ui.reward = EZPZReward:new(); ui.reward:initialise(); ui.reward:addToUIManager(); ui.reward:setVisible(false)
    ui.admin = EZPZAdmin:new(); ui.admin:initialise(); ui.admin:addToUIManager(); ui.admin:setVisible(false)
end

function EZPZ.openAdmin()
    create()
    local a = EZPZ.ui.admin
    a:setX((screenW() - a.width) / 2)
    a:setY(screenH() * 0.25)
    a:setVisible(true)
    a:bringToTop()
end

-- An "EZPZ Server Tools" button in the game's Admin panel, just above Close.
if ISAdminPanelUI and not EZPZ.adminPanelHooked then
    EZPZ.adminPanelHooked = true
    local originalCreate = ISAdminPanelUI.create
    function ISAdminPanelUI:create()
        originalCreate(self)
        local ok, err = pcall(function()
            local c = self.cancel
            if not c then return end
            local b = ISButton:new(11, c:getY(), self:getWidth() - 22, c:getHeight(), "EZPZ Server Tools", self, function() EZPZ.openAdmin() end)
            b:initialise()
            b:instantiate()
            b.borderColor = self.buttonBorderColor
            b.tooltip = "Preview the panels EZPZ Server Tools shows players"
            self:addChild(b)
            c:setY(b:getBottom() + 10)
            self:setHeight(c:getBottom() + 11)
        end)
        if not ok then print("[EZPZ] couldn't add the Admin panel button: " .. tostring(err)) end
    end
end

-- Works out what to show from the current state. Called a few times a second and on every change.
function EZPZ.refresh()
    local ui = EZPZ.ui
    if not ui.bar then return end
    local s, elapsed = current()

    -- the bar: season, wipe, restart
    local parts, red, show = {}, false, false
    if s then
        local restart, wipe = left(s.restart, elapsed), left(s.wipe, elapsed)
        local near = false
        if s.season and s.season > 0 then
            near = isNear(s, elapsed)
            if near then
                parts[#parts + 1] = "THE END IS NEAR   " .. clock(wipe)
            else
                parts[#parts + 1] = "Season " .. s.season .. (s.day and s.day > 0 and (" · day " .. s.day) or "")
                if wipe >= 0 then parts[#parts + 1] = "wipe in " .. clock(wipe) end
            end
            show = true
        elseif wipe >= 0 and wipe < 7 * 86400 then   -- a week's notice
            parts[#parts + 1] = "Wipe in " .. clock(wipe)
            show = true
        end
        if restart >= 0 and restart < 3600 then
            parts[#parts + 1] = "restart in " .. clock(restart)
            show = true
        end
        red = near
        -- the banner, once per session when THE END IS NEAR is on
        if near and not s.preview and EZPZ.bannerShownFor ~= s.season then
            EZPZ.bannerShownFor = s.season
            EZPZ.showBanner("Season " .. s.season .. " ends with the wipe in " .. clock(wipe))
        end
    end
    local text = table.concat(parts, "   ·   ")
    if #parts > 0 then text = string.upper(string.sub(text, 1, 1)) .. string.sub(text, 2) end
    ui.bar.text = text
    ui.bar.red = red
    ui.bar.clickable = s ~= nil and s.season ~= nil and s.season > 0
    local w = textW(UIFont.Medium, text) + 36
    ui.bar:setWidth(w)
    ui.bar:setX((screenW() - w) / 2)
    ui.bar:setY(barY(ui.bar.height))
    ui.bar:setVisible(show)

    if ui.banner:isVisible() and getTimestampMs() - ui.banner.shownAt > 10000 then ui.banner:setVisible(false) end

    -- the season panel, under the bar
    local open = EZPZ.seasonOpen and s ~= nil and s.season ~= nil and s.season > 0
    if open then
        ui.season.lines = seasonLines(s, elapsed)
        local h = 16
        local w2 = 330
        for _, l in ipairs(ui.season.lines) do
            h = h + fontH(l.font or UIFont.Small) + (l.gap or 2)
            w2 = math.max(w2, textW(l.font or UIFont.Small, l.text) + (l.right and textW(l.font or UIFont.Small, l.right) + 40 or 24))
        end
        ui.season:setWidth(w2)
        ui.season:setHeight(h)
        ui.season:setX((screenW() - w2) / 2)
        ui.season:setY(ui.bar.y - h - 6)   -- stacks upward from the bar
    end
    ui.season:setVisible(open)

    -- a reward notice, once
    local n = s and s.notice
    if n and (s.preview or not EZPZ.noticesSeen[n.item .. "|" .. n.title]) then
        if not ui.reward:isVisible() then
            ui.reward.item = n.item
            ui.reward.title = n.title
            ui.reward:setWidth(math.max(440, textW(UIFont.Medium, n.item) + PAD * 4, textW(UIFont.Small, n.title) + PAD * 4))
            if ui.reward.ok then ui.reward.ok:setX((ui.reward.width - ui.reward.ok.width) / 2) end
            ui.reward:setX((screenW() - ui.reward.width) / 2)
            ui.reward:setY(screenH() * 0.35)
            ui.reward:setVisible(true)
            ui.reward:bringToTop()
        end
    elseif ui.reward:isVisible() and not (s and s.notice) then
        ui.reward:setVisible(false)
    end
end

local function onServerCommand(module, command, args)
    if module ~= "EZPZ" or command ~= "state" then return end
    EZPZ.state = args
    EZPZ.stateAt = getTimestampMs()
    EZPZ.refresh()
end

local lastRefresh = 0
local function onTick()
    local now = getTimestampMs()
    if now - lastRefresh < 250 then return end
    lastRefresh = now
    local ok, err = pcall(EZPZ.refresh)
    if not ok then print("[EZPZ] UI: " .. tostring(err)) end
end

local function onGameStart()
    if not isClient() then return end   -- multiplayer only: the server half supplies the data
    create()
    Events.OnTick.Add(onTick)
end

Events.OnServerCommand.Add(onServerCommand)
Events.OnGameStart.Add(onGameStart)
