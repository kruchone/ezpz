using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Live player condition from the EZPZ Server Tools mod: a "Players right now" list on Home and a "Right now"
    // line on the character sheet.
    public partial class MainForm
    {
        LiveSnapshot live;
        bool liveReading;
        ListView lvLive;
        Label lblLiveNote, lblSheetLive;

        void BuildLiveSection(Page p)
        {
            p.H2("Players right now");
            lvLive = new ListView
            {
                Size = new Size(Page.Wide, 130), View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.75f),
                ShowItemToolTips = true,   // hovering a row shows everything, since long lists get cut off in the columns
            };
            lvLive.Columns.Add("Player", 120);
            lvLive.Columns.Add("Health", 60);
            lvLive.Columns.Add("Injuries", 170);
            lvLive.Columns.Add("Moodles", 230);
            lvLive.Columns.Add("Needs", 200);
            lvLive.Columns.Add("Where", 110);
            lvLive.DoubleClick += (s, e) => { var u = LiveSelectedMapUrl(); if (u != null) Util.OpenUrl(u); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open on the Players page", null, (s, e) => { if (lvLive.SelectedItems.Count == 1) ShowPlayer((string)lvLive.SelectedItems[0].Tag); });
            menu.Items.Add("View this spot on pzmap.org", null, (s, e) => { var u = LiveSelectedMapUrl(); if (u != null) Util.OpenUrl(u); });
            lvLive.ContextMenuStrip = menu;
            p.Add(lvLive, 134);
            lblLiveNote = Fixed(p, 24, Theme.Muted);
        }

        string LiveSelectedMapUrl()
        {
            if (lvLive.SelectedItems.Count != 1 || live == null) return null;
            var lp = live.Find((string)lvLive.SelectedItems[0].Tag);
            return lp == null ? null : Util.MapUrl((int)lp.X + "," + (int)lp.Y);
        }

        void ShowPlayer(string name)
        {
            ShowPage(PPlayers);
            var item = lvPlayers.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals((string)i.Tag, name, StringComparison.OrdinalIgnoreCase));
            if (item != null) { item.Selected = true; item.EnsureVisible(); }
        }

        // Every 5 seconds while the server runs.
        void LiveTick()
        {
            if (liveReading) return;
            if (server.State != ServerState.Running) { if (live != null) { live = null; RefreshLive(); } return; }
            liveReading = true;
            var settings = S;
            Task.Run(() => LiveFeed.Read(settings)).ContinueWith(t => Ui(() =>
            {
                liveReading = false;
                if (settings != S) return;
                live = t.Exception == null ? t.Result : null;
                RefreshLive();
            }));
        }

        void RefreshLive()
        {
            if (currentPage == PPlayers) UpdateSheetLive();
            if (lvLive == null) return;
            lvLive.BeginUpdate();
            lvLive.Items.Clear();
            bool fresh = live != null && live.Fresh && server.State == ServerState.Running;
            if (fresh)
            {
                foreach (var lp in live.Players.OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
                {
                    var it = new ListViewItem(new[]
                    {
                        lp.Username, LiveFeed.HealthText(lp), LiveFeed.InjuryText(lp), LiveFeed.MoodleText(lp), LiveFeed.NeedsText(lp),
                        lp.Vehicle != "" ? VehicleName(lp.Vehicle) : (int)lp.X + ", " + (int)lp.Y,
                    }) { Tag = lp.Username, UseItemStyleForSubItems = false };
                    it.ToolTipText = lp.Username + ": " + LiveFeed.HealthText(lp) + " health\n" + LiveFeed.InjuryText(lp) + "\n" + LiveFeed.MoodleText(lp) + "\n" + LiveFeed.NeedsText(lp);
                    it.SubItems[1].ForeColor = lp.Health < 50 ? Theme.Danger : lp.Health < 80 ? Theme.Warn : Theme.Accent;
                    if (lp.Infected || lp.Bitten > 0 || lp.Bleeding > 0) it.SubItems[2].ForeColor = Theme.Danger;
                    else if (lp.Deep + lp.Scratched + lp.Fractures + lp.Burns > 0) it.SubItems[2].ForeColor = Theme.Warn;
                    else it.SubItems[2].ForeColor = Theme.Muted;
                    if (lp.Moodles.Count == 0) it.SubItems[3].ForeColor = Theme.Muted;
                    else if (lp.Moodles.Values.Max() >= 3) it.SubItems[3].ForeColor = Theme.Warn;
                    lvLive.Items.Add(it);
                }
            }
            lvLive.EndUpdate();

            if (server.State != ServerState.Running) lblLiveNote.Text = "Shown while the server is running.";
            else if (live == null) lblLiveNote.Text = "Add the EZPZ Server Tools mod (Mods page) to see each player's health, injuries and moodles here.";
            else if (!live.Fresh) lblLiveNote.Text = "No report from EZPZ Server Tools since " + live.Written.ToString("HH:mm:ss") + ". Is it still in the server's mod list?";
            else lblLiveNote.Text = live.Players.Count == 0 ? "Nobody online." : "Reported by each player's game every 10 seconds. Double-click a player to see where they are.";
        }

        string VehicleName(string script)
        {
            var c = catalog.FindVehicle(script);
            return c != null ? c.Name : script;
        }

        // The character sheet's "Right now" line for the selected player.
        void UpdateSheetLive()
        {
            if (lblSheetLive == null) return;
            var lp = Sel != null && live != null && live.Fresh && server.State == ServerState.Running ? live.Find(Sel) : null;
            if (lp == null) { lblSheetLive.Text = ""; return; }
            string where = lp.Vehicle != "" ? (lp.Driving ? "driving a " : "in a ") + VehicleName(lp.Vehicle) : lp.Asleep ? "asleep" : "on foot";
            double temp;
            string t = lp.Stats.TryGetValue("TEMPERATURE", out temp) ? "  ·  " + temp.ToString("0.0") + " °C" : "";
            lblSheetLive.Text = "Right now:  ❤ " + LiveFeed.HealthText(lp) + "  ·  " + where + t
                + "\n" + LiveFeed.InjuryText(lp)
                + "\n" + LiveFeed.MoodleText(lp) + "  ·  " + LiveFeed.NeedsText(lp);
            lblSheetLive.ForeColor = lp.Infected || lp.Health < 50 ? Theme.Danger : lp.Health < 80 || lp.Bleeding > 0 ? Theme.Warn : Theme.Accent;
        }
    }
}
