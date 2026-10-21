using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ezpz
{
    // Map page: the world map from the server's own map images, with players on it. Online players come from
    // EZPZ Server Tools (live), everyone else from the world save (last saved position).
    public partial class MainForm
    {
        Panel pgMap;
        MapView mapView;
        CheckBox chkMapOffline;
        ComboBox cbMapFind;
        Label lblMapInfo, lblMapNote;
        string mapLoadedFor;

        static readonly Color MapLive = Color.FromArgb(120, 220, 100), MapSaved = Color.FromArgb(150, 160, 175), MapDead = Color.FromArgb(235, 95, 85);

        void BuildMap()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(20, 14, 20, 14) };
            content.Controls.Add(p);
            pgMap = p;

            mapView = new MapView { Dock = DockStyle.Fill };
            mapView.MarkerClicked += m => ShowMapInfo(m);
            mapView.RightClickedAt += (x, y) => MapMenu(x, y).Show(mapView, mapView.PointToClient(Cursor.Position));
            p.Controls.Add(mapView);

            lblMapInfo = new Label { Dock = DockStyle.Bottom, Height = 44, ForeColor = Theme.Text, Padding = new Padding(0, 8, 0, 0), UseMnemonic = false };
            MakeToolsHint(lblMapInfo);
            p.Controls.Add(lblMapInfo);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
            bar.Controls.Add(new Label { Text = "Find", AutoSize = true, Margin = new Padding(0, 9, 6, 0) });
            cbMapFind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 200, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 5, 12, 0) };
            cbMapFind.SelectionChangeCommitted += (s, e) => BeginInvoke((Action)(() => MapFind(cbMapFind.Text)));
            cbMapFind.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; MapFind(cbMapFind.Text); } };
            bar.Controls.Add(cbMapFind);
            chkMapOffline = Theme.Check("Show offline players", true);
            chkMapOffline.Margin = new Padding(0, 8, 12, 0);
            chkMapOffline.CheckedChanged += (s, e) => RefreshMapMarkers();
            bar.Controls.Add(chkMapOffline);
            var zin = Theme.Btn("+"); zin.Click += (s, e) => mapView.Zoom(1.5);
            var zout = Theme.Btn("−"); zout.Click += (s, e) => mapView.Zoom(1 / 1.5);
            var all = Theme.Btn("Whole map"); all.Click += (s, e) => mapView.CenterOn(10000, 8000, 1.0 / 24);
            bar.Controls.AddRange(new Control[] { zin, zout, all });
            lblMapNote = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(8, 10, 0, 0) };
            bar.Controls.Add(lblMapNote);
            p.Controls.Add(bar);

            p.Controls.Add(new Label
            {
                Text = "Drag to move, scroll to zoom. Find a player or type coordinates like 10600, 9700. Right-click the map for more.",
                Dock = DockStyle.Top, Height = 26, ForeColor = Theme.Muted
            });
            p.Controls.Add(new Label { Text = "Map", Font = Theme.H1, Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text });
        }

        // The base map and any map mods' images, loaded the first time the page opens (and again after switching servers).
        void EnsureMapLoaded()
        {
            string key = S.InstallDir + "|" + IniFile.Load(S.IniPath).Get("Map", "");
            if (mapLoadedFor == key) return;
            mapLoadedFor = key;
            var zips = new List<string>();
            string base0 = Path.Combine(S.InstallDir, "media", "maps", "Muldraugh, KY", "pyramid.zip");
            if (File.Exists(base0)) zips.Add(base0);
            // map mods: their map folders (from Map=) that ship their own images
            var folders = ModConfig.Split(IniFile.Load(S.IniPath).Get("Map", "")).Where(f => !f.Equals(ModConfig.VanillaMap, StringComparison.OrdinalIgnoreCase));
            var mods = AllMods.ToList();
            foreach (var folder in folders)
            {
                foreach (var m in mods.Where(x => x.MapFolders.Contains(folder)))
                {
                    var hit = Directory.GetFiles(m.Folder, "pyramid.zip", SearchOption.AllDirectories)
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), folder, StringComparison.OrdinalIgnoreCase));
                    if (hit != null && !zips.Contains(hit)) zips.Add(hit);
                }
            }
            mapView.Problem = zips.Count == 0 ? "The map comes from the server install, which isn't there yet. Install the server on the Setup page." : null;
            mapView.Load(zips);
            lblMapNote.Text = zips.Count > 1 ? "Base map + " + (zips.Count - 1) + " map mod" + (zips.Count == 2 ? "" : "s") : "";
        }

        void RefreshMapMarkers()
        {
            if (mapView == null) return;
            string sel = mapView.Selected != null ? mapView.Selected.Id : null;
            var list = new List<MapView.Marker>();
            bool fresh = live != null && live.Fresh && server.State == ServerState.Running;
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fresh)
                foreach (var lp in live.Players)
                {
                    shown.Add(lp.Username);
                    list.Add(new MapView.Marker
                    {
                        Id = lp.Username, Label = lp.Username, X = lp.X, Y = lp.Y, Color = MapLive,
                        Tip = lp.Username + " (online)\n❤ " + LiveFeed.HealthText(lp) + " · " + LiveFeed.InjuryText(lp) + "\n" + LiveFeed.MoodleText(lp)
                            + (lp.Vehicle != "" ? "\n" + (lp.Driving ? "Driving a " : "In a ") + VehicleName(lp.Vehicle) : ""),
                    });
                }
            if (chkMapOffline.Checked || !fresh)
                foreach (var c in dbChars.Values)
                {
                    if (shown.Contains(c.Username) || (c.X == 0 && c.Y == 0)) continue;
                    bool on = onlineNow.Contains(c.Username);
                    if (!chkMapOffline.Checked && !on) continue;
                    list.Add(new MapView.Marker
                    {
                        Id = c.Username, Label = c.Username + (c.Dead ? " ✝" : ""), X = c.X, Y = c.Y, Hollow = true,
                        Color = c.Dead ? MapDead : on ? MapLive : MapSaved,
                        Tip = c.Username + (c.Name != "" ? " · " + c.Name : "") + (c.Dead ? " (dead)" : "") + "\nLast saved position" + (on ? " (online; add EZPZ Server Tools for live positions)" : ""),
                    });
                }
            mapView.Markers = list;
            mapView.Selected = list.FirstOrDefault(m => m.Id == sel);
            mapView.Invalidate();

            string cur = cbMapFind.Text;
            var names = list.Select(m => m.Id).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!names.SequenceEqual(cbMapFind.Items.Cast<string>()))
            {
                cbMapFind.Items.Clear();
                cbMapFind.Items.AddRange(names);
                cbMapFind.Text = cur;
            }
            ShowMapInfo(mapView.Selected);
        }

        static readonly Regex Coords = new Regex(@"^\s*(-?\d+(?:\.\d+)?)\s*[, x]\s*(-?\d+(?:\.\d+)?)\s*$");

        void MapFind(string text)
        {
            var c = Coords.Match(text ?? "");
            if (c.Success)
            {
                mapView.CenterOn(double.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture), 1);
                return;
            }
            var m = mapView.Markers.FirstOrDefault(x => x.Id.Equals((text ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (m == null) return;
            mapView.Selected = m;
            mapView.CenterOn(m.X, m.Y, 1);
            ShowMapInfo(m);
        }

        void ShowMapInfo(MapView.Marker m)
        {
            if (m == null)
            {
                string why;
                bool missing = ToolsStatus(out why) == ToolsLevel.Missing;
                lblMapInfo.Text = "Green: online now · grey ring: last saved position · red: dead. Click a player for details."
                    + (missing ? "\n" + ToolsHintText("Live positions (instead of the last saved one)") : "");
                lblMapInfo.ForeColor = Theme.Muted;
                return;
            }
            lblMapInfo.Text = m.Tip.Replace("\n", "   ·   ") + "   ·   at " + (int)m.X + ", " + (int)m.Y;
            lblMapInfo.ForeColor = Theme.Text;
        }

        ContextMenuStrip MapMenu(double x, double y)
        {
            string at = (int)x + ", " + (int)y;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Copy coordinates (" + at + ")", null, (s, e) => { try { Clipboard.SetText(at); } catch { } });
            menu.Items.Add("View this spot on pzmap.org", null, (s, e) => Util.OpenUrl(Util.MapUrl(at)));
            var sel = mapView.Selected;
            if (sel != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Open " + sel.Id + " on the Players page", null, (s, e) => ShowPlayer(sel.Id));
            }
            return menu;
        }

        void OnMapShown()
        {
            EnsureMapLoaded();
            if (dbChars.Count == 0) LoadCharacters();
            RefreshMapMarkers();
        }
    }
}
