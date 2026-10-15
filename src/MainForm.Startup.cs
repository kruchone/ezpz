using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ezpz
{
    // Loads everything the pages need while the splash is up, so opening a page is instant.
    // Never starts the server. Each step fails on its own and is reported in plain words.
    public partial class MainForm
    {
        public List<string> Preload(Action<string, int> progress)
        {
            var problems = new List<string>();
            var steps = new List<KeyValuePair<string, Action>>
            {
                Step("Checking whether the server is already running", () => server.DetectExisting()),
                Step("Reading the server's settings", () => { LoadConfig(); LoadDiscordState(); LoadSeasons(); RefreshDiscord(); }),
                Step("Loading players", () =>
                {
                    roster = GameData.ReadRoster(S);
                    lastLogRead = DateTime.Now;
                    perkNames = Characters.LoadNames(S.InstallDir);
                    perkNamesFor = S.InstallDir;
                    chars = Characters.Read(S);
                    dbChars = ServerDb.ReadCharacters(S);
                    bans = ServerDb.ReadBans(S);
                    tickets = ServerDb.ReadTickets(S);
                    bool rebuilt;
                    feed.Poll(S, out rebuilt);
                }),
                Step("Loading mods", () => RefreshMods(false)),
                Step("Loading item and vehicle names", () => { catalog = ItemCatalog.Load(S); ShowPickedName(itemPicker, lblItemName); ShowPickedName(vehiclePicker, lblVehicleName); ShowRewardNames(); }),
                Step("Loading world rules (sandbox)", () => RefreshSandbox(false)),
                Step("Reading the world's radio and memory history", () =>
                {
                    DateTime when;
                    string khz = GameData.FindEbs(S, out when);
                    if (khz != null) { ebsKhz = khz; ebsWhen = when; }
                    ebsLookedUp = true;
                    gcReader.Read(S.GcLogPath);
                }),
                Step("Listing backups", () => RefreshBackupList()),
                Step("Reading the server's logs", () => { logAll = logData.Load(S, perkNames); logLoadedAt = DateTime.Now; FilterLogs(); }),
                Step("Checking Windows Firewall", () => firewallOk = CheckFirewall()),
                // Windows only creates a control's on-screen window the first time it's shown, which is what made
                // big pages (Sandbox has ~1,100 controls) freeze on first open. Create them all now, behind the splash.
                Step("Reticulating splines", () => { CreateHandles(this); foreach (Control pg in content.Controls) pg.PerformLayout(); }),
            };

            for (int i = 0; i < steps.Count; i++)
            {
                progress(steps[i].Key + "…", (int)(100.0 * i / steps.Count));
                try { steps[i].Value(); }
                catch (Exception ex)
                {
                    string what = steps[i].Key.Substring(0, 1).ToLower() + steps[i].Key.Substring(1);
                    problems.Add("Failed while " + what.Replace("Checking", "checking") + ": " + Explain(ex));
                    AppLog.Error("startup step \"" + steps[i].Key + "\"", ex);
                }
            }
            progress("Ready", 100);

            if (problems.Count > 0)
            {
                foreach (var p in problems) Log("[ezpz] Startup problem: " + p);
            }
            return problems;
        }

        static KeyValuePair<string, Action> Step(string name, Action a) { return new KeyValuePair<string, Action>(name, a); }

        // Turns common exceptions into something a person can act on.
        static string Explain(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Windows denied access to a file or folder. " + ex.Message;
            if (ex is DirectoryNotFoundException || ex is FileNotFoundException) return "a file or folder is missing. " + ex.Message;
            if (ex is IOException) return "a file is in use or unreadable. " + ex.Message;
            return ex.Message;
        }

        static void CreateHandles(Control c)
        {
            var h = c.Handle;
            foreach (Control k in c.Controls) CreateHandles(k);
        }

        bool CheckFirewall()
        {
            return (RunHidden("netsh", "advfirewall firewall show rule name=\"" + FirewallRule + "\"") == 0
                 || RunHidden("netsh", "advfirewall firewall show rule name=\"" + LegacyFirewallRule + "\"") == 0)
                && RunHidden("netsh", "advfirewall firewall show rule name=\"" + PrivateTcpRule + "\"") == 0;
        }
    }
}
