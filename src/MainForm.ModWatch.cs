using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Mod update watcher: while the server runs, compare its installed Workshop items with the Workshop
    // (free Steam Web API, no key) and act on updates. Players can't join while the server's copy of a mod
    // is older than theirs, and the server only fetches mod updates when it starts.
    public partial class MainForm
    {
        static readonly string[] ModActions = { "Just tell me", "Restart after a 5-minute warning", "Restart when nobody is online" };

        ComboBox cbModAction;
        Label lblModWatch;
        Button btnCancelModRestart;
        List<string> modsOutdated = new List<string>();
        List<string> modsMissing = new List<string>();
        DateTime lastModCheck = DateTime.MinValue;
        DateTime? modRestartAt;
        bool modChecking, modRestartWhenEmpty;
        string modCheckError;
        readonly HashSet<int> modWarned = new HashSet<int>();

        Control BuildModWatchRow()
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            row.Controls.Add(new Label { Text = "When a mod updates:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
            cbModAction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 10, 0) };
            cbModAction.Items.AddRange(ModActions);
            cbModAction.SelectedIndex = Math.Max(0, Math.Min(2, S.ModUpdateAction));
            cbModAction.SelectedIndexChanged += (s, e) => { S.ModUpdateAction = cbModAction.SelectedIndex; S.Save(); };
            var check = Theme.Btn("Check now");
            check.Click += async (s, e) => await CheckModUpdates(true);
            btnCancelModRestart = Theme.Btn("Cancel restart", Theme.DangerBtn);
            btnCancelModRestart.Visible = false;
            btnCancelModRestart.Click += (s, e) => CancelModRestart();
            lblModWatch = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(10, 8, 0, 0) };
            row.Controls.AddRange(new Control[] { cbModAction, check, btnCancelModRestart, lblModWatch });
            return row;
        }

        // Called every second from OnSecond.
        void ModWatchTick()
        {
            if (server.State != ServerState.Running)
            {
                if (modRestartAt != null || modRestartWhenEmpty) { modRestartAt = null; modRestartWhenEmpty = false; UpdateModWatchLabel(); }
                return;
            }
            if (!modChecking && busy == null && (DateTime.Now - lastModCheck).TotalMinutes >= Math.Max(2, S.ModCheckMinutes)
                && (DateTime.Now - server.StartedAt).TotalMinutes >= 2)
            {
                var _ = CheckModUpdates(false);
            }

            if (modRestartAt != null)
            {
                var left = modRestartAt.Value - DateTime.Now;
                if (left.TotalMinutes <= 1 && modWarned.Add(1))
                    Cmd("servermsg \"A mod was updated. Server restarting in 1 minute to install it.\"");
                if (left.TotalSeconds <= 0 && busy == null)
                {
                    modRestartAt = null;
                    Log("[ezpz] Restarting to install mod updates.");
                    var __ = RestartFlow(false);
                }
                if (ticks % 5 == 0) UpdateModWatchLabel();
            }
            else if (modRestartWhenEmpty)
            {
                if (ticks % 30 == 0) PollLiveStats();
                double players;
                bool known;
                lock (liveStats) known = liveStats.TryGetValue("players", out players);
                if (known && players == 0 && (DateTime.Now - lastStatsPoll).TotalSeconds < 20 && busy == null)
                {
                    modRestartWhenEmpty = false;
                    Log("[ezpz] Nobody is online. Restarting to install mod updates.");
                    var __ = RestartFlow(false);
                }
            }
        }

        async Task CheckModUpdates(bool userAsked)
        {
            if (modChecking) return;
            var ids = ModConfig.Split(IniFile.Load(S.IniPath).Get("WorkshopItems", ""));
            if (ids.Count == 0)
            {
                modsOutdated.Clear(); modsMissing.Clear(); modCheckError = null; lastModCheck = DateTime.Now;
                UpdateModWatchLabel();
                if (userAsked) Info("This server has no Workshop mods.");
                return;
            }
            modChecking = true;
            UpdateModWatchLabel();
            try
            {
                List<string> missing = null;
                var outdated = await Task.Run(() => Workshop.Outdated(S, ids, out missing));
                bool fresh = outdated.Except(modsOutdated).Any();
                modsOutdated = outdated;
                modsMissing = missing;
                modCheckError = null;
                if (outdated.Count > 0 && (fresh || userAsked)) OnModsOutdated();
            }
            catch (Exception ex) { modCheckError = ex.Message; }
            finally
            {
                modChecking = false;
                lastModCheck = DateTime.Now;
                UpdateModWatchLabel();
                if (currentPage == PMods) RebuildModList();
            }
        }

        void OnModsOutdated()
        {
            string names = string.Join(", ", modsOutdated.Select(TitleOf));
            Log("[ezpz] Mod update available on the Workshop: " + names);
            string plan = server.State != ServerState.Running || S.ModUpdateAction == 0 ? "The server needs a restart to install it."
                : S.ModUpdateAction == 1 ? "Restarting in 5 minutes to install it." : "It'll restart to install it as soon as nobody is online.";
            Notify(DiscordEvent.Updates, "🧩 Mod update for " + ServerTitle(), names + "\n" + plan, DcAmber);
            if (server.State != ServerState.Running) return;
            switch (S.ModUpdateAction)
            {
                case 1:
                    if (modRestartAt == null)
                    {
                        modRestartAt = DateTime.Now.AddMinutes(5);
                        modWarned.Clear();
                        Cmd("servermsg \"A mod was updated. Server restarting in 5 minutes to install it.\"");
                        Log("[ezpz] Restarting in 5 minutes to install the update (players were warned).");
                    }
                    break;
                case 2:
                    modRestartWhenEmpty = true;
                    Log("[ezpz] Will restart to install the update as soon as nobody is online.");
                    PollLiveStats();
                    break;
            }
        }

        void CancelModRestart()
        {
            if (modRestartAt != null) Cmd("servermsg \"The restart has been cancelled.\"");
            modRestartAt = null;
            modRestartWhenEmpty = false;
            Log("[ezpz] Mod update restart cancelled. Players with the newer mod version can't join until the server restarts.");
            UpdateModWatchLabel();
        }

        string ModWatchSummary()
        {
            if (modChecking) return "Checking the Workshop for mod updates…";
            if (modCheckError != null) return "Couldn't check for mod updates: " + modCheckError;
            if (lastModCheck == DateTime.MinValue) return server.State == ServerState.Running ? "Mod updates are checked every " + Math.Max(2, S.ModCheckMinutes) + " minutes while the server runs." : "Mod updates are checked while the server runs.";
            string s;
            if (modsOutdated.Count > 0) s = modsOutdated.Count + " mod update" + (modsOutdated.Count == 1 ? "" : "s") + " waiting: " + string.Join(", ", modsOutdated.Select(TitleOf));
            else s = "Mods are up to date (checked " + FriendlyTime(lastModCheck) + ")";
            if (modRestartAt != null)
            {
                var left = modRestartAt.Value - DateTime.Now;
                s += ". Restarting in " + Math.Max(0, (int)left.TotalMinutes) + ":" + Math.Max(0, left.Seconds).ToString("00");
            }
            else if (modRestartWhenEmpty) s += ". Will restart when nobody is online";
            if (modsMissing.Count > 0) s += ". Not downloaded yet: " + string.Join(", ", modsMissing.Select(TitleOf));
            return s + ".";
        }

        void UpdateModWatchLabel()
        {
            if (lblModWatch == null) return;
            lblModWatch.Text = ModWatchSummary();
            lblModWatch.ForeColor = modsOutdated.Count > 0 ? Theme.Warn : Theme.Muted;
            btnCancelModRestart.Visible = modRestartAt != null || modRestartWhenEmpty;
            if (currentPage == PHome) UpdateUpdateLabel();
        }
    }
}
