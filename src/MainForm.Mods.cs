using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Mods page: add Workshop items (links, ids or collections), download them with SteamCMD,
    // tick which mod ids to load, and write WorkshopItems= / Mods= / Map= in the B42 format.
    public partial class MainForm
    {
        ListView lvMods;
        TextBox tbModPaste, tbModIssues;
        Label lblModsState;

        readonly List<string> wsOrder = new List<string>();                 // WorkshopItems=, in order
        readonly List<string> enabledMods = new List<string>();             // Mods=, in load order
        readonly Dictionary<string, WorkshopItem> wsInfo = new Dictionary<string, WorkshopItem>();
        readonly Dictionary<string, List<ModEntry>> wsMods = new Dictionary<string, List<ModEntry>>();
        ModConfig.RunStatus lastRun = new ModConfig.RunStatus();
        bool modsDirty, rebuildingMods, modsLoaded;

        void BuildMods()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(20, 14, 20, 14) };
            content.Controls.Add(p);
            pgMods = p;

            lvMods = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10f)
            };
            lvMods.Columns.Add("Mod ID", 190);
            lvMods.Columns.Add("Name", 200);
            lvMods.Columns.Add("Workshop item", 230);
            lvMods.Columns.Add("Status", 170);
            lvMods.Columns.Add("Needs", 140);
            lvMods.ItemCheck += OnModItemCheck;
            lvMods.ItemChecked += OnModItemChecked;
            // When the list first appears, Windows replays every tick as untick-then-tick. Ignore those,
            // or they would mark the page as changed and move ticked mods to the end of the load order.
            lvMods.HandleCreated += (s, e) => { rebuildingMods = true; BeginInvoke((Action)(() => rebuildingMods = false)); };
            p.Controls.Add(lvMods);

            // issues + save row under the list
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 216, Padding = new Padding(0, 10, 0, 0) };
            tbModIssues = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Base
            };
            var saveRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            var save = Theme.Btn("Save to server config", Theme.AccentBtn);
            save.Click += (s, e) => SaveMods();
            var revert = Theme.Btn("Discard changes");
            revert.Click += (s, e) => { if (!modsDirty || Confirm("Discard your unsaved mod changes?")) RefreshMods(true); };
            lblModsState = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 8, 0, 0) };
            saveRow.Controls.AddRange(new Control[] { save, revert, lblModsState });
            bottom.Controls.Add(tbModIssues);
            bottom.Controls.Add(BuildModWatchRow());
            bottom.Controls.Add(saveRow);
            p.Controls.Add(bottom);

            // toolbar under the paste box
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            var dl = Theme.Btn("Download / update all");
            dl.Click += async (s, e) => await DownloadMods(wsOrder.ToList());
            var up = Theme.Btn("▲ Load earlier");
            up.Click += (s, e) => MoveMod(-1);
            var down = Theme.Btn("▼ Load later");
            down.Click += (s, e) => MoveMod(1);
            var remove = Theme.Btn("Remove item", Theme.DangerBtn);
            remove.Click += (s, e) => RemoveWorkshopItem();
            var page = Theme.Btn("Workshop page");
            page.Click += (s, e) => { var w = SelectedWorkshopId(); if (w != null) Process.Start("https://steamcommunity.com/sharedfiles/filedetails/?id=" + w); };
            var folder = Theme.Btn("Open files");
            folder.Click += (s, e) =>
            {
                var w = SelectedWorkshopId();
                if (w != null && Workshop.IsDownloaded(S, w)) OpenFolder(Path.Combine(Workshop.ContentDir(S), w, "mods"));
            };
            tools.Controls.AddRange(new Control[] { dl, up, down, remove, page, folder });
            p.Controls.Add(tools);

            // paste box
            var addRow = new Panel { Dock = DockStyle.Top, Height = 36 };
            tbModPaste = Theme.Box("");
            tbModPaste.Dock = DockStyle.Fill;
            tbModPaste.Font = new Font("Segoe UI", 10.5f);
            tbModPaste.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await AddWorkshopItems(); } };
            var add = Theme.Btn("Add", Theme.AccentBtn);
            add.Dock = DockStyle.Right;
            add.Margin = new Padding(0);
            add.Click += async (s, e) => await AddWorkshopItems();
            addRow.Controls.Add(tbModPaste);
            addRow.Controls.Add(add);
            p.Controls.Add(addRow);

            p.Controls.Add(new Label
            {
                Text = "Paste Steam Workshop links, item numbers or a collection link (several at once is fine), then Add. "
                     + "ezpz downloads them, finds their mod IDs, and you tick the ones to load. Players' games download the same mods automatically when they join.",
                Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Muted
            });
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
            p.Controls.Add(BuildServerToolsCard());
            p.Controls.Add(new Label { Text = "Mods", Font = Theme.H1, Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text });
        }

        // ---------- loading

        void RefreshMods(bool force)
        {
            if (modsDirty && !force) { RebuildModList(); return; }
            modsLoaded = true;
            var ini = IniFile.Load(S.IniPath);
            wsOrder.Clear();
            wsOrder.AddRange(ModConfig.Split(ini.Get("WorkshopItems", "")));
            enabledMods.Clear();
            foreach (var m in ModConfig.Split(ini.Get("Mods", "")).Select(ModConfig.NormalizeId))
                if (!enabledMods.Contains(m)) enabledMods.Add(m);
            modsDirty = false;
            ScanAll();
            RebuildModList();
            ResolveTitles(wsOrder);
            UpdateModWatchLabel();
            RefreshServerToolsCard();
        }

        void ScanAll()
        {
            foreach (var w in wsOrder)
            {
                try { wsMods[w] = ModScanner.Scan(S, w); } catch { wsMods[w] = new List<ModEntry>(); }
            }
            try { localMods = ModScanner.ScanLocal(S); } catch { localMods = new List<ModEntry>(); }
            lastRun = ModConfig.ReadLastRun(S);
        }

        void ResolveTitles(IEnumerable<string> ids)
        {
            var need = ids.Where(i => !wsInfo.ContainsKey(i)).ToList();
            if (need.Count == 0) return;
            Task.Run(() => Workshop.GetDetails(need)).ContinueWith(t =>
            {
                if (t.Exception != null) return;   // offline: titles just stay blank
                Ui(() => { foreach (var kv in t.Result) wsInfo[kv.Key] = kv.Value; RebuildModList(); });
            });
        }

        List<ModEntry> localMods = new List<ModEntry>();

        IEnumerable<ModEntry> AllMods { get { return wsOrder.Where(wsMods.ContainsKey).SelectMany(w => wsMods[w]).Concat(localMods); } }

        string TitleOf(string ws)
        {
            WorkshopItem it;
            return wsInfo.TryGetValue(ws, out it) && it.Title != "" ? it.Title + "  (" + ws + ")" : ws;
        }

        // ---------- list

        void RebuildModList()
        {
            if (lvMods == null) return;
            rebuildingMods = true;
            string keep = lvMods.SelectedItems.Count == 1 ? lvMods.SelectedItems[0].Name : null;
            lvMods.BeginUpdate();
            lvMods.Items.Clear();

            var all = AllMods.ToList();
            var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in all) if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;

            // enabled mods first, in load order
            int n = 1;
            foreach (var id in enabledMods)
            {
                ModEntry m;
                if (byId.TryGetValue(id, out m)) AddModRow(m, true, n++);
                else
                {
                    var it = lvMods.Items.Add(new ListViewItem(new[] { id, "", "(not in any Workshop item or the server's mods folder)", "Missing", "" }));
                    it.Name = "mod:" + id; it.Checked = true; it.ForeColor = Theme.Danger;
                    it.Tag = null;
                    n++;
                }
            }
            // then the rest, grouped by Workshop item
            foreach (var w in wsOrder)
            {
                List<ModEntry> mods;
                if (!wsMods.TryGetValue(w, out mods) || mods.Count == 0)
                {
                    var it = lvMods.Items.Add(new ListViewItem(new[] { "—", "", TitleOf(w), Workshop.IsDownloaded(S, w) ? "No mods found in item" : "Not downloaded yet", "" }));
                    it.Name = "ws:" + w; it.Tag = w; it.ForeColor = Theme.Warn;
                    continue;
                }
                foreach (var m in mods) if (!enabledMods.Contains(m.Id)) AddModRow(m, false, 0);
            }
            foreach (var m in localMods) if (!enabledMods.Contains(m.Id)) AddModRow(m, false, 0);

            if (keep != null && lvMods.Items.ContainsKey(keep)) lvMods.Items[keep].Selected = true;
            lvMods.EndUpdate();
            rebuildingMods = false;
            UpdateModIssues();
        }

        void AddModRow(ModEntry m, bool enabled, int order)
        {
            string status;
            Color color;
            if (!m.HasB42) { status = "No B42 version"; color = Theme.Warn; }
            else if (modsOutdated.Contains(m.WorkshopId)) { status = "Update available"; color = Theme.Warn; }
            else if (enabled && lastRun.Loaded.Contains(m.Id)) { status = "Loaded"; color = Theme.Accent; }
            else if (enabled) { status = modsDirty ? "On (save + restart)" : "On (restart to load)"; color = Theme.Text; }
            else { status = "Off"; color = Theme.Muted; }
            string id = enabled ? order + ".  " + m.Id : m.Id;
            if (m.Local) status = "Local · " + status;
            var it = new ListViewItem(new[] { id, m.Name, m.Local ? "Server's mods folder (players need their own copy)" : TitleOf(m.WorkshopId), status, string.Join(", ", m.Requires) });
            it.Name = "mod:" + m.Id;
            it.Tag = m;
            it.Checked = enabled;
            it.ForeColor = color;
            lvMods.Items.Add(it);
        }

        void OnModItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (rebuildingMods) return;
            var it = lvMods.Items[e.Index];
            if (it.Tag is string) e.NewValue = CheckState.Unchecked;   // placeholder row for an item without mods
        }

        void OnModItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (rebuildingMods) return;
            string id = e.Item.Name.StartsWith("mod:") ? e.Item.Name.Substring(4) : null;
            if (id == null) return;
            // Windows also raises this when the list first appears; only a real change counts.
            if (e.Item.Checked == enabledMods.Contains(id)) return;
            if (e.Item.Checked && !enabledMods.Contains(id)) enabledMods.Add(id);
            if (!e.Item.Checked) enabledMods.Remove(id);
            modsDirty = true;
            BeginInvoke((Action)RebuildModList);   // re-sort after the click finishes
        }

        string SelectedWorkshopId()
        {
            if (lvMods.SelectedItems.Count != 1) { Warn("Select a mod or Workshop item in the list first."); return null; }
            var t = lvMods.SelectedItems[0].Tag;
            var m = t as ModEntry;
            if (m != null && m.Local) { Warn(m.Name + " is a local mod from the server's mods folder, not a Workshop item."); return null; }
            if (m != null) return m.WorkshopId;
            return t as string;
        }

        void MoveMod(int dir)
        {
            if (lvMods.SelectedItems.Count != 1) return;
            string name = lvMods.SelectedItems[0].Name;
            if (!name.StartsWith("mod:")) return;
            string id = name.Substring(4);
            int i = enabledMods.IndexOf(id);
            if (i < 0) { Warn("Only enabled (ticked) mods have a load order."); return; }
            int j = i + dir;
            if (j < 0 || j >= enabledMods.Count) return;
            enabledMods.RemoveAt(i);
            enabledMods.Insert(j, id);
            modsDirty = true;
            RebuildModList();
        }

        void RemoveWorkshopItem()
        {
            var w = SelectedWorkshopId();
            if (w == null) return;
            if (!Confirm("Remove " + TitleOf(w) + " from the server's mod list? (Its downloaded files are left alone.)")) return;
            wsOrder.Remove(w);
            List<ModEntry> mods;
            if (wsMods.TryGetValue(w, out mods)) foreach (var m in mods) enabledMods.Remove(m.Id);
            wsMods.Remove(w);
            modsDirty = true;
            RebuildModList();
        }

        // ---------- adding & downloading

        async Task AddWorkshopItems()
        {
            var ids = Workshop.ParseIds(tbModPaste.Text);
            if (ids.Count == 0) { Warn("Paste a Steam Workshop link or item number first."); return; }
            if (!BeginBusy("Looking up Workshop items…")) return;
            List<string> added = new List<string>();
            try
            {
                var collections = await Task.Run(() => Workshop.GetCollections(ids));
                var expanded = new List<string>();
                foreach (var id in ids)
                {
                    List<string> kids;
                    if (collections.TryGetValue(id, out kids)) { Log("[ezpz] Collection " + id + " contains " + kids.Count + " item(s)."); expanded.AddRange(kids); }
                    else expanded.Add(id);
                }
                var details = await Task.Run(() => Workshop.GetDetails(expanded.Distinct().ToList()));
                foreach (var id in expanded.Distinct())
                {
                    WorkshopItem it;
                    if (details.TryGetValue(id, out it)) wsInfo[id] = it;
                    if (it != null && !it.Resolved) { Log("[ezpz] Workshop item " + id + " wasn't found (removed or private). Skipped."); continue; }
                    if (it != null && !it.IsB42Tagged) Log("[ezpz] Note: \"" + it.Title + "\" isn't tagged for Build 42 on the Workshop.");
                    if (!wsOrder.Contains(id)) { wsOrder.Add(id); added.Add(id); }
                }
                tbModPaste.Clear();
            }
            catch (Exception ex) { Warn("Couldn't reach the Steam Workshop: " + ex.Message); }
            finally { EndBusy(); }

            if (added.Count == 0) { RebuildModList(); return; }
            modsDirty = true;
            Log("[ezpz] Added " + added.Count + " Workshop item(s). Downloading...");
            await DownloadMods(added);
            // Turn on mods from newly added items that contain exactly one mod; multi-mod items need a choice.
            foreach (var w in added)
            {
                List<ModEntry> mods;
                if (wsMods.TryGetValue(w, out mods) && mods.Count == 1 && !enabledMods.Contains(mods[0].Id)) enabledMods.Add(mods[0].Id);
            }
            RebuildModList();
        }

        async Task DownloadMods(List<string> ids)
        {
            if (ids.Count == 0) { Info("No Workshop items to download yet. Paste a link above and click Add."); return; }
            if (!File.Exists(S.StartBat)) { Warn("Install the server first (Setup page). Mods are downloaded into the server's folder."); return; }
            if (!BeginBusy("Downloading " + ids.Count + " mod item(s)…")) return;
            try
            {
                var failed = await Workshop.Download(S, ids, Log);
                if (failed.Count > 0) Log("[ezpz] These items didn't download: " + string.Join(", ", failed.Select(TitleOf)));
                else Log("[ezpz] Workshop download finished.");
            }
            catch (Exception ex) { Log("[steamcmd] ERROR: " + ex.Message); }
            finally { EndBusy(); }
            ScanAll();
            RebuildModList();
        }

        // ---------- checks & saving

        void UpdateModIssues()
        {
            var lines = ComputeModIssues();
            if (lines.Count == 0)
                lines.Add(wsOrder.Count == 0 && localMods.Count == 0 ? "No mods yet." : "No problems found.");
            tbModIssues.Text = string.Join("\r\n", lines);
            ShowModState();
        }

        // Shared with the Home page. Lines starting with the warning sign are real problems; others are notes.
        List<string> ComputeModIssues()
        {
            if (!modsLoaded) RefreshMods(false);
            var lines = new List<string>();
            var all = AllMods.ToList();
            var known = new HashSet<string>(all.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var enabledSet = new HashSet<string>(enabledMods, StringComparer.OrdinalIgnoreCase);

            foreach (var m in all.Where(x => enabledSet.Contains(x.Id)))
            {
                foreach (var r in m.Requires)
                {
                    if (enabledSet.Contains(r)) continue;
                    lines.Add("⚠ " + m.Id + " needs " + r + (known.Contains(r) ? ". Tick it in the list." : ". Add the Workshop item that contains it."));
                }
                if (!m.HasB42) lines.Add("⚠ " + m.Id + " has no Build 42 version and may not work on this server.");
                foreach (var f in m.MapFolders) lines.Add("Map: " + m.Id + " adds the map \"" + f + "\". It's added to Map= automatically when you save.");
            }
            foreach (var id in enabledMods.Where(id => !known.Contains(id)))
                lines.Add("⚠ " + id + " is turned on but isn't in any downloaded Workshop item or the server's mods folder. Add or download its item, or untick it.");
            foreach (var g in all.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.WorkshopId).Distinct().Count() > 1))
                lines.Add("⚠ Mod ID " + g.Key + " appears in more than one Workshop item.");
            foreach (var w in wsOrder)
            {
                List<ModEntry> mods;
                if (!wsMods.TryGetValue(w, out mods)) continue;
                if (mods.Count > 1 && !mods.Any(m => enabledSet.Contains(m.Id)))
                    lines.Add("• " + TitleOf(w) + " contains " + mods.Count + " mods. Tick the one(s) you want.");
                else if (mods.Count == 1 && !enabledSet.Contains(mods[0].Id))
                    lines.Add("• " + TitleOf(w) + " is listed but its mod is off. Players still download it, so remove the item if you don't need it.");
                else if (!Workshop.IsDownloaded(S, w))
                    lines.Add("• " + TitleOf(w) + " isn't downloaded yet. Click Download / update all.");
            }
            foreach (var p in lastRun.Problems) lines.Add("Server said: " + p);

            return lines;
        }

        void ShowModState()
        {
            int on = enabledMods.Count;
            lblModsState.Text = wsOrder.Count + " Workshop item(s) · " + on + " mod(s) on" +
                (modsDirty ? "  ·  unsaved changes" : "") +
                (lastRun.Found ? "" : "  ·  server hasn't run yet");
            lblModsState.ForeColor = modsDirty ? Theme.Warn : Theme.Muted;
        }

        void SaveMods()
        {
            if (!File.Exists(S.IniPath)) { Warn("There's no server config yet. Click 'Generate default config' on the Setup page first."); return; }
            var ini = IniFile.Load(S.IniPath);
            var all = AllMods.ToList();
            var enabledMaps = enabledMods.SelectMany(id => all.Where(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(1)).SelectMany(m => m.MapFolders);
            string ws = string.Join(";", wsOrder);
            string mods = ModConfig.FormatMods(enabledMods);
            string map = ModConfig.BuildMap(ini.Get("Map", ModConfig.VanillaMap), enabledMaps, all.SelectMany(m => m.MapFolders));
            ini.Set("WorkshopItems", ws);
            ini.Set("Mods", mods);
            ini.Set("Map", map);
            ini.Save();
            modsDirty = false;
            Log("[ezpz] Saved mods: WorkshopItems=" + ws + "  Mods=" + mods + "  Map=" + map);
            RebuildModList();
            if (server.State == ServerState.Running && Confirm("Saved. Mods change when the server restarts. Restart it now?"))
            {
                var _ = RestartFlow(false);
            }
        }
    }
}
