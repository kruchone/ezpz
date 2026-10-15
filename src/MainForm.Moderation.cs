using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Players page extras from the server's database: support tickets players file in game, and the ban list.
    public partial class MainForm
    {
        List<ServerDb.Ticket> tickets = new List<ServerDb.Ticket>();
        List<ServerDb.Ban> bans = new List<ServerDb.Ban>();
        Button btnTickets, btnBans;
        bool ticketsLoading, bansLoading;

        void UpdateModerationButtons()
        {
            if (btnTickets == null) return;
            int open = tickets.Count(t => t.Answers.Count == 0);
            btnTickets.Text = "Tickets (" + tickets.Count + (open > 0 && open != tickets.Count ? ", " + open + " new" : "") + ")";
            btnTickets.Visible = tickets.Count > 0;
            btnTickets.BackColor = open > 0 ? Color.FromArgb(150, 110, 30) : Theme.Card;
            btnBans.Text = "Bans (" + bans.Count + ")";
            btnBans.Visible = bans.Count > 0;
        }

        // ================================================================== tickets

        // Every 30 seconds: new tickets are logged, flash the window and (optionally) go to Discord.
        void TicketTick()
        {
            if (ticketsLoading) return;
            ticketsLoading = true;
            var settings = S;
            Task.Run(() => ServerDb.ReadTickets(settings)).ContinueWith(t => Ui(() =>
            {
                ticketsLoading = false;
                if (t.Exception != null || settings != S) return;
                tickets = t.Result;
                long newest = tickets.Count == 0 ? 0 : tickets.Max(x => x.Id);
                if (dstate.TicketSeenId < 0) dstate.TicketSeenId = newest;   // first look: don't announce old tickets
                foreach (var tk in tickets.Where(x => x.Id > dstate.TicketSeenId))
                {
                    Log("[ezpz] New ticket from " + tk.Author + ": " + tk.Message);
                    Notify(DiscordEvent.Tickets, "🎫 Ticket from " + tk.Author, tk.Message + "\n\nAnswer it in ezpz (Players page → Tickets) or in game.", DcAmber);
                    FlashWindow();
                }
                if (newest > dstate.TicketSeenId) { dstate.TicketSeenId = newest; SaveDiscordState(); }
                UpdateModerationButtons();
            }));
        }

        void ShowTickets()
        {
            var f = Dialog("Player tickets", 760, 480);
            var lv = new ListView { Dock = DockStyle.Top, Height = 170, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            lv.Columns.Add("#", 50);
            lv.Columns.Add("From", 140);
            lv.Columns.Add("Ticket", 420);
            lv.Columns.Add("Answered", 100);
            var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Side, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            var answer = Theme.Box("");
            answer.Dock = DockStyle.Bottom;
            var hint = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Muted, Text = "Your answer (the player sees it in the game's ticket window, from \"admin\"):", Padding = new Padding(0, 6, 0, 0) };
            Action fill = () =>
            {
                long keep = lv.SelectedItems.Count == 1 ? ((ServerDb.Ticket)lv.SelectedItems[0].Tag).Id : -1;
                lv.Items.Clear();
                foreach (var t in tickets)
                {
                    var it = new ListViewItem(new[] { t.Id.ToString(), t.Author, t.Message, t.Answers.Count == 0 ? "no" : "yes" }) { Tag = t };
                    if (t.Answers.Count == 0) it.ForeColor = Theme.Warn;
                    lv.Items.Add(it);
                    if (t.Id == keep) it.Selected = true;
                }
                if (lv.SelectedItems.Count == 0 && lv.Items.Count > 0) lv.Items[0].Selected = true;
            };
            lv.SelectedIndexChanged += (s, e) =>
            {
                if (lv.SelectedItems.Count != 1) { detail.Text = ""; return; }
                var t = (ServerDb.Ticket)lv.SelectedItems[0].Tag;
                detail.Text = t.Author + ":" + Environment.NewLine + t.Message
                    + string.Concat(t.Answers.Select(a => Environment.NewLine + Environment.NewLine + a.Author + " answered:" + Environment.NewLine + a.Message));
            };
            Func<ServerDb.Ticket> sel = () => lv.SelectedItems.Count == 1 ? (ServerDb.Ticket)lv.SelectedItems[0].Tag : null;
            Action reload = () => { tickets = ServerDb.ReadTickets(S); UpdateModerationButtons(); fill(); };

            var send = Theme.Btn("Answer", Theme.AccentBtn);
            send.Click += (s, e) =>
            {
                var t = sel();
                string msg = answer.Text.Trim();
                if (t == null || msg == "") { MessageBox.Show(f, "Select a ticket and type an answer.", "ezpz"); return; }
                try { ServerDb.AnswerTicket(S, t.Id, "admin", msg); }
                catch (Exception ex) { AppLog.Error("answering a ticket", ex); MessageBox.Show(f, ex.Message, "ezpz"); return; }
                Log("[ezpz] Answered " + t.Author + "'s ticket #" + t.Id + ".");
                answer.Clear();
                reload();
            };
            var del = Theme.Btn("Delete ticket", Theme.DangerBtn);
            del.Click += (s, e) =>
            {
                var t = sel();
                if (t == null || MessageBox.Show(f, "Delete " + t.Author + "'s ticket #" + t.Id + " and its answers?", "ezpz", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                try { ServerDb.DeleteTicket(S, t.Id); }
                catch (Exception ex) { AppLog.Error("deleting a ticket", ex); MessageBox.Show(f, ex.Message, "ezpz"); return; }
                Log("[ezpz] Deleted " + t.Author + "'s ticket #" + t.Id + ".");
                reload();
            };
            var close = Theme.Btn("Close");
            close.Click += (s, e) => f.Close();
            var buttons = Buttons(send, del, close);

            var lower = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            lower.Controls.Add(detail);
            lower.Controls.Add(hint);
            lower.Controls.Add(answer);
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            body.Controls.Add(lower);
            body.Controls.Add(lv);
            f.Controls.Add(body);
            f.Controls.Add(buttons);
            fill();
            f.ShowDialog(this);
        }

        // ================================================================== bans

        void RefreshBans()
        {
            if (bansLoading) return;
            bansLoading = true;
            var settings = S;
            Task.Run(() => ServerDb.ReadBans(settings)).ContinueWith(t => Ui(() =>
            {
                bansLoading = false;
                if (t.Exception == null && settings == S) { bans = t.Result; UpdateModerationButtons(); }
            }));
        }

        static string BanKind(ServerDb.Ban b) { return b.Kind == "account" ? "Account" : b.Kind == "steam" ? "Steam ID" : "IP address"; }

        void ShowBans()
        {
            var f = Dialog("Bans", 760, 400);
            var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            lv.Columns.Add("Banned", 100);
            lv.Columns.Add("Who", 190);
            lv.Columns.Add("Account", 140);
            lv.Columns.Add("Reason", 270);
            Action fill = () =>
            {
                lv.Items.Clear();
                foreach (var b in bans)
                    lv.Items.Add(new ListViewItem(new[] { BanKind(b), b.Value, b.Username, b.Reason }) { Tag = b });
                if (lv.Items.Count > 0) lv.Items[0].Selected = true;
            };
            var hint = new Label
            {
                Dock = DockStyle.Top, Height = 52, ForeColor = Theme.Muted, Padding = new Padding(10, 8, 10, 0),
                Text = "An account ban stops that username; Steam ID and IP bans stop anyone from that Steam account or address. Unbanning needs the server running."
            };
            var unban = Theme.Btn("Unban", Theme.AccentBtn);
            unban.Click += async (s, e) =>
            {
                if (lv.SelectedItems.Count != 1) return;
                var b = (ServerDb.Ban)lv.SelectedItems[0].Tag;
                if (server.State != ServerState.Running) { MessageBox.Show(f, "Start the server first. The game applies unbans itself.", "ezpz"); return; }
                if (MessageBox.Show(f, "Unban " + BanKind(b).ToLower() + " " + b.Value + (b.Username != "" && b.Username != b.Value ? " (" + b.Username + ")" : "") + "?", "ezpz", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                Cmd((b.Kind == "account" ? "unbanuser" : b.Kind == "steam" ? "unbanid" : "unbanip") + " \"" + Q(b.Value) + "\"");
                await Task.Delay(1500);
                bans = await Task.Run(() => ServerDb.ReadBans(S));
                UpdateModerationButtons();
                RefreshPlayers(true);
                fill();
                if (bans.Count == 0) f.Close();
            };
            var close = Theme.Btn("Close");
            close.Click += (s, e) => f.Close();
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
            body.Controls.Add(lv);
            f.Controls.Add(body);
            f.Controls.Add(hint);
            f.Controls.Add(Buttons(unban, close));
            fill();
            f.ShowDialog(this);
        }

        // ---------- small dialog helpers

        Form Dialog(string title, int w, int h)
        {
            return new Form
            {
                Text = title, Size = new Size(w, h), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, ForeColor = Theme.Text,
                Font = Theme.Base, MinimizeBox = false, MaximizeBox = false, ShowIcon = false, ShowInTaskbar = false,
            };
        }

        static FlowLayoutPanel Buttons(params Control[] cs)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8, 10, 8, 0) };
            p.Controls.AddRange(cs);
            return p;
        }
    }
}
