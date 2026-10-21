using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // EZPZ Server Tools, ezpz's companion mod: whether it's installed and reporting, a card on the Mods page that
    // adds it in one click, and the "Needs EZPZ Server Tools" hints on the pages that depend on it.
    public partial class MainForm
    {
        const string ToolsWorkshopId = "3823711409", ToolsModId = "EZPZServerTools";
        static readonly Version ToolsMinVersion = new Version(0, 2, 0);   // the first with the in-game panels (countdown, seasons)

        const string ToolsUnlocks = "Live health, injuries and moodles on Home and the Players page, live positions on the Map, "
            + "the restart and wipe countdown above players' hotbars, and seasons in game (the ladder, THE END IS NEAR, reward notices, "
            + "and the Slayer, Explorer and PvP categories).";

        enum ToolsLevel { Active, Waiting, Old, Missing }

        // Where it stands, in a sentence, from the server config and the mod's latest report.
        ToolsLevel ToolsStatus(out string text)
        {
            var ini = File.Exists(S.IniPath) ? IniFile.Load(S.IniPath) : null;
            bool enabled = ini != null && ModConfig.Split(ini.Get("Mods", "")).Select(ModConfig.NormalizeId).Any(m => m.Equals(ToolsModId, StringComparison.OrdinalIgnoreCase));
            bool fromWorkshop = ini != null && ModConfig.Split(ini.Get("WorkshopItems", "")).Contains(ToolsWorkshopId);
            bool localCopy = Directory.Exists(Path.Combine(S.DataDir, "mods", ToolsModId));
            bool running = server.State == ServerState.Running;
            string source = fromWorkshop ? "from the Workshop" : localCopy ? "from the server's mods folder (players need their own copy)" : "";
            string both = fromWorkshop && localCopy ? " A copy is also in the server's mods folder: delete it so everyone loads the Workshop version." : "";

            if (running && live != null && live.Fresh)
            {
                Version v;
                if (Version.TryParse(live.ModVersion, out v) && v < ToolsMinVersion)
                {
                    text = "Version " + live.ModVersion + " is running, which is too old for the in-game countdown and seasons. Update it to " + ToolsMinVersion.ToString(3) + " or later." + both;
                    return ToolsLevel.Old;
                }
                text = "Active: version " + (live.ModVersion != "" ? live.ModVersion : "?") + (source != "" ? ", " + source : "") + ". Last report " + live.Written.ToString("HH:mm:ss") + "." + both;
                return ToolsLevel.Active;
            }
            if (!enabled)
            {
                text = "Not installed.";
                return ToolsLevel.Missing;
            }
            if (!running) text = "Added " + source + ". It starts reporting when the server runs." + both;
            else if (live == null) text = "Added, but it hasn't reported yet. Restart the server if you added it while it was running." + both;
            else text = "Added, but there's been no report since " + live.Written.ToString("HH:mm:ss") + ". Is it still loading?" + both;
            return ToolsLevel.Waiting;
        }

        static Color ToolsColor(ToolsLevel l) { return l == ToolsLevel.Active ? Theme.Accent : l == ToolsLevel.Missing ? Theme.Muted : Theme.Warn; }

        // A label that says a feature needs the mod and opens its card on the Mods page when clicked.
        void MakeToolsHint(Label l)
        {
            l.Cursor = Cursors.Hand;
            l.Click += (s, e) => { if (l.Text.Contains("EZPZ Server Tools")) ShowPage(PMods); };
        }

        string ToolsHintText(string what)
        {
            return what + " needs EZPZ Server Tools. Click to add it (Mods page).";
        }

        // ---------- the card on the Mods page

        Label lblToolsStatus;
        Button btnToolsAdd;

        Control BuildServerToolsCard()
        {
            var card = new Panel { Dock = DockStyle.Top, Height = 122, BackColor = Theme.Card, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 230, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            btnToolsAdd = Theme.Btn("Add EZPZ Server Tools", Theme.AccentBtn);
            btnToolsAdd.Width = 220;
            btnToolsAdd.Click += async (s, e) => await AddServerTools();
            var page = Theme.Btn("Workshop page", Theme.Bg);
            page.Width = 220;
            page.Click += (s, e) => Util.OpenUrl("https://steamcommunity.com/sharedfiles/filedetails/?id=" + ToolsWorkshopId);
            buttons.Controls.AddRange(new Control[] { btnToolsAdd, page });
            var text = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            lblToolsStatus = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, AutoEllipsis = true };
            text.Controls.Add(new Label { Text = "Unlocks: " + ToolsUnlocks, Dock = DockStyle.Fill, ForeColor = Theme.Muted });
            text.Controls.Add(lblToolsStatus);
            text.Controls.Add(new Label { Text = "EZPZ Server Tools: ezpz's companion mod", Dock = DockStyle.Top, Height = 24, Font = Theme.Bold, ForeColor = Theme.Text });
            card.Controls.Add(text);
            card.Controls.Add(buttons);
            return card;
        }

        void RefreshServerToolsCard()
        {
            if (lblToolsStatus == null) return;
            string text;
            var level = ToolsStatus(out text);
            lblToolsStatus.Text = text;
            lblToolsStatus.ForeColor = ToolsColor(level);
            bool onWorkshop = File.Exists(S.IniPath) && ModConfig.Split(IniFile.Load(S.IniPath).Get("WorkshopItems", "")).Contains(ToolsWorkshopId) && wsOrder.Contains(ToolsWorkshopId);
            btnToolsAdd.Visible = !onWorkshop;
            btnToolsAdd.Text = Directory.Exists(Path.Combine(S.DataDir, "mods", ToolsModId)) ? "Use the Workshop version" : "Add EZPZ Server Tools";
        }

        // Adds the Workshop item, downloads it, turns the mod on and saves (unless other mod edits are waiting).
        async Task AddServerTools()
        {
            if (!File.Exists(S.IniPath)) { Warn("There's no server config yet. Click 'Generate default config' on the Setup page first."); return; }
            if (!modsLoaded) RefreshMods(true);
            bool otherEdits = modsDirty;
            if (!wsOrder.Contains(ToolsWorkshopId)) wsOrder.Add(ToolsWorkshopId);
            if (!enabledMods.Any(m => m.Equals(ToolsModId, StringComparison.OrdinalIgnoreCase))) enabledMods.Add(ToolsModId);
            modsDirty = true;
            Log("[ezpz] Adding EZPZ Server Tools (Workshop item " + ToolsWorkshopId + ").");
            await DownloadMods(new List<string> { ToolsWorkshopId });
            // a copy in the server's mods folder would clash with the Workshop one (same mod ID)
            string local = Path.Combine(S.DataDir, "mods", ToolsModId);
            if (Directory.Exists(local) && Confirm("There's also a copy of EZPZ Server Tools in the server's mods folder:\n" + local
                + "\n\nDelete it, so the server and players all load the Workshop version?"))
            {
                try { Directory.Delete(local, true); Log("[ezpz] Deleted the local copy of EZPZ Server Tools: " + local); }
                catch (Exception ex) { Warn("Couldn't delete it: " + ex.Message + "\nDelete the folder yourself before restarting."); }
                try { localMods = ModScanner.ScanLocal(S); } catch { }
            }
            RebuildModList();
            if (otherEdits) Info("EZPZ Server Tools is added and ticked. You have other unsaved mod changes, so click 'Save to server config' when you're ready, then restart the server.");
            else SaveMods();   // asks to restart if the server is running
            RefreshServerToolsCard();
        }
    }
}
