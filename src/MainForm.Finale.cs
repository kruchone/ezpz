using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ezpz
{
    // THE END IS NEAR, the finale: what happens in the world during a season's last hours. It escalates in
    // three stages (the dead are restless, the storm gets worse, the last hour) and every effect is optional:
    //   horde waves near random players, an eternal storm, the helicopter, "last helicopter out" (an
    //   extraction point: be there at the wipe), and double points. All through ordinary server commands;
    //   nothing persists, since the world is wiped right after.
    public partial class MainForm
    {
        static readonly string[] FinaleLevels = { "Light", "Heavy", "Brutal" };
        // per level, per stage 1..3: minutes between waves, and zombies per wave
        static readonly int[,] HordeEvery = { { 60, 45, 30 }, { 40, 30, 20 }, { 30, 20, 12 } };
        static readonly int[,] HordeSize = { { 8, 12, 20 }, { 15, 25, 40 }, { 25, 40, 70 } };
        static readonly int[] StormEvery = { 60, 40, 20 };      // minutes between storm (re)starts, by stage
        static readonly int[] ChopperEvery = { 120, 60, 30 };   // minutes between helicopters, by stage

        DateTime nextHordeAt, nextStormAt, nextChopperAt, nextThunderAt, nextExtractionReminder;
        readonly Random finaleRng = new Random();

        bool FinaleAny { get { return S.FinaleHordes || S.FinaleStorm || S.FinaleChopper || S.FinaleExtraction || S.FinaleDoublePoints; } }

        // 0 = not in the finale; 1 = from THE END IS NEAR; 2 = the second half; 3 = the last hour (or last tenth)
        int FinaleStage()
        {
            var end = SeasonEndAt();
            if (Season == null || end == null) return 0;
            double left = (end.Value - DateTime.Now).TotalHours, total = Math.Max(1, S.SeasonEndHours);
            if (left > total || left <= 0) return 0;
            if (left <= Math.Max(1, total * 0.1)) return 3;
            if (left <= total / 2) return 2;
            return 1;
        }

        // Called from SeasonTick (every 10 s) while the server runs.
        void FinaleTick(SeasonRecord season)
        {
            int stage = FinaleStage();
            if (stage == 0)
            {
                nextHordeAt = nextStormAt = nextChopperAt = nextThunderAt = nextExtractionReminder = DateTime.MinValue;
                return;
            }
            if (season.FinaleStart == "") { season.FinaleStart = DateTime.Now.ToString("o"); seasonsDirty = true; }
            if (!FinaleAny) return;
            var now = DateTime.Now;

            // a new stage: say so (once), and bring the next events forward
            if (stage > season.FinaleStage)
            {
                season.FinaleStage = stage;
                SaveSeasons();
                FinaleAnnounce(stage);
                nextHordeAt = nextStormAt = nextChopperAt = now.AddSeconds(30);
            }
            var targets = FinaleTargets();
            if (targets.Count == 0) return;   // nobody to play it for: effects wait
            int s = stage - 1, lvl = Math.Max(0, Math.Min(2, S.FinaleHordeLevel));

            if (S.FinaleHordes && now >= nextHordeAt)
            {
                if (nextHordeAt != DateTime.MinValue)
                {
                    string who = targets[finaleRng.Next(targets.Count)];
                    int size = Math.Min(500, HordeSize[lvl, s] + (targets.Count - 1) * HordeSize[lvl, s] / 4);
                    Cmd("createhorde " + size + " \"" + Q(who) + "\"");
                    Log("[finale] A horde of " + size + " near " + who + ".");
                }
                nextHordeAt = now.AddMinutes(HordeEvery[lvl, s] * (0.75 + finaleRng.NextDouble() * 0.5));   // not like clockwork
            }
            if (S.FinaleStorm && now >= nextStormAt)
            {
                Cmd("startstorm 24");
                Log("[finale] The storm rolls on.");
                nextStormAt = now.AddMinutes(StormEvery[s]);
            }
            if (S.FinaleStorm && stage >= 2 && now >= nextThunderAt)
            {
                string who = targets[finaleRng.Next(targets.Count)];
                Cmd((stage == 3 && finaleRng.Next(3) == 0 ? "lightning" : "thunder") + " \"" + Q(who) + "\"");
                nextThunderAt = now.AddMinutes(stage == 3 ? 3 + finaleRng.Next(5) : 8 + finaleRng.Next(10));
            }
            if (S.FinaleChopper && now >= nextChopperAt)
            {
                if (nextChopperAt != DateTime.MinValue) { Cmd("chopper"); Log("[finale] The helicopter is back."); }
                nextChopperAt = now.AddMinutes(ChopperEvery[s] * (0.75 + finaleRng.NextDouble() * 0.5));
            }
            if (S.FinaleExtraction) ExtractionTick(season, stage);
        }

        // Online players who are playing (with the mod: those it reports, so nobody on the character screen).
        List<string> FinaleTargets()
        {
            if (live != null && live.Fresh) return live.Players.Select(p => p.Username).ToList();
            return onlineSince.Where(kv => (DateTime.Now - kv.Value).TotalMinutes >= 5).Select(kv => kv.Key).ToList();
        }

        void FinaleAnnounce(int stage)
        {
            var parts = new List<string>();
            if (stage == 1)
            {
                if (S.FinaleHordes) parts.Add("The dead are restless. Hordes are on the move.");
                if (S.FinaleStorm) parts.Add("A storm is coming, and it isn't leaving.");
                if (S.FinaleDoublePoints) parts.Add("Until the end, kills and skills count double on the ladder.");
            }
            else if (stage == 2)
            {
                if (S.FinaleStorm) parts.Add("The storm is getting worse.");
                if (S.FinaleHordes) parts.Add("The hordes are growing.");
            }
            else parts.Add("This is it. The last hour belongs to the dead.");
            if (parts.Count == 0) return;
            foreach (var m in parts) Cmd("servermsg \"" + Q(m) + "\"");
            Log("[finale] Stage " + stage + ": " + string.Join(" ", parts));
            if (stage > 1) Notify(DiscordEvent.Seasons, "☠️ " + ServerTitle() + ": " + parts[0], string.Join(" ", parts.Skip(1)), DcRed);
        }

        // ---------- last helicopter out

        // The point is picked when stage 2 begins, so there's time to get there: the admin's choice, else the
        // area most players have been to this season (with the mod), else a random living character's last spot.
        void ExtractionTick(SeasonRecord season, int stage)
        {
            if (stage < 2) return;
            if (season.Extraction == "")
            {
                var p = PickExtraction(season);
                if (p == null) return;
                season.Extraction = p.Value.X + "," + p.Value.Y;
                SaveSeasons();
                Log("[finale] Last helicopter out: " + season.Extraction + " (within " + S.FinaleExtractionRadius + " tiles).");
                Notify(DiscordEvent.Seasons, "🚁 Last helicopter out on " + ServerTitle(),
                    "Be within " + S.FinaleExtractionRadius + " m of " + season.Extraction + " when the wipe hits: [map](" + Util.MapUrl(season.Extraction) + "). Everyone there is evacuated, which counts on the ladder.", DcAmber);
                nextExtractionReminder = DateTime.MinValue;
            }
            if (DateTime.Now >= nextExtractionReminder)
            {
                Cmd("servermsg \"Last helicopter out: be within " + S.FinaleExtractionRadius + " m of " + season.Extraction + " when the wipe hits. Evacuees score on the ladder.\"");
                nextExtractionReminder = DateTime.Now.AddMinutes(stage == 3 ? 10 : 30);
            }
        }

        Point? PickExtraction(SeasonRecord season)
        {
            var m = Regex.Match(S.FinaleExtractionPoint ?? "", @"^\s*(\d+)\s*[, ]\s*(\d+)\s*$");
            if (m.Success) return new Point(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            // the 100x100 area the most players have visited this season
            var busiest = season.Players.Values.SelectMany(p => p.Cells.Distinct()).GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (busiest != null)
            {
                var xy = busiest.Key.Split(',');
                return new Point(int.Parse(xy[0]) * 100 + 50, int.Parse(xy[1]) * 100 + 50);
            }
            try
            {
                var alive = ServerDb.ReadCharacters(S).Values.Where(c => !c.Dead && (c.X != 0 || c.Y != 0)).ToList();
                if (alive.Count > 0) { var c = alive[finaleRng.Next(alive.Count)]; return new Point((int)c.X, (int)c.Y); }
            }
            catch { }
            return null;
        }

        // Living characters inside the extraction circle (positions as saved when the server stopped).
        static List<string> Evacuees(AppSettings s, SeasonRecord season, Dictionary<string, ServerDb.Character> chars)
        {
            var m = Regex.Match(season.Extraction ?? "", @"^(\d+),(\d+)$");
            if (!s.FinaleExtraction || !m.Success) return new List<string>();
            double x = double.Parse(m.Groups[1].Value), y = double.Parse(m.Groups[2].Value), r = Math.Max(5, s.FinaleExtractionRadius);
            return chars.Values.Where(c => !c.Dead && (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y) <= r * r).Select(c => c.Username).ToList();
        }

        // ---------- settings (on the Seasons page)

        CheckBox chkFinHordes, chkFinStorm, chkFinChopper, chkFinExtract, chkFinDouble;
        ComboBox cbFinLevel;
        NumericUpDown nFinRadius;
        TextBox tbFinPoint;

        void BuildFinaleSection(Page p)
        {
            p.H2("THE END IS NEAR: the finale");
            p.P("What happens in the world during the season's last hours. It builds in three stages: the start of THE END IS NEAR, its second half, and the last hour. Players are told at each stage. The world is wiped right after, so none of it lasts.");
            chkFinHordes = Theme.Check("Horde waves: zombies spawn near a random player, more often and bigger as the end gets closer", false);
            p.Add(chkFinHordes, 24);
            cbFinLevel = p.Row("    How hard", Combo(FinaleLevels), 140);
            p.Hint("Light: a wave about every hour, 8–20 zombies. Heavy: every 20–40 minutes, 15–40. Brutal: every 12–30 minutes, 25–70. A quarter more per extra player online.");
            chkFinStorm = Theme.Check("Eternal storm: a thunderstorm that never ends, with thunder and lightning near players later on", false);
            p.Add(chkFinStorm, 24);
            chkFinChopper = Theme.Check("The helicopter keeps coming (every 2 hours, then every hour, then every half hour). It drags hordes behind it.", false);
            p.Add(chkFinChopper, 24);
            chkFinExtract = Theme.Check("Last helicopter out: from the second half, an extraction point is announced. Living characters there at the wipe are evacuated.", false);
            p.Add(chkFinExtract, 24);
            nFinRadius = p.Row("    Within (tiles)", Theme.Num(10, 500, 50), 80);
            tbFinPoint = p.Row("    Extraction point", Theme.Box(""), 140);
            p.Hint("x,y from the Map page, or leave empty: then it's the area most players have been to this season. Evacuees score highest for Final Stand and are named in the results.");
            chkFinDouble = Theme.Check("Double points: during THE END IS NEAR, level-ups, zombie kills and player kills count double on the ladder", false);
            p.Add(chkFinDouble, 24);
        }

        void LoadFinaleControls()
        {
            if (chkFinHordes == null) return;
            chkFinHordes.Checked = S.FinaleHordes;
            cbFinLevel.SelectedIndex = Math.Max(0, Math.Min(2, S.FinaleHordeLevel));
            chkFinStorm.Checked = S.FinaleStorm;
            chkFinChopper.Checked = S.FinaleChopper;
            chkFinExtract.Checked = S.FinaleExtraction;
            nFinRadius.Value = Math.Max(10, Math.Min(500, S.FinaleExtractionRadius));
            tbFinPoint.Text = S.FinaleExtractionPoint;
            chkFinDouble.Checked = S.FinaleDoublePoints;
        }

        bool FinaleDirty()
        {
            if (chkFinHordes == null) return false;
            return chkFinHordes.Checked != S.FinaleHordes || cbFinLevel.SelectedIndex != S.FinaleHordeLevel || chkFinStorm.Checked != S.FinaleStorm
                || chkFinChopper.Checked != S.FinaleChopper || chkFinExtract.Checked != S.FinaleExtraction || (int)nFinRadius.Value != S.FinaleExtractionRadius
                || tbFinPoint.Text.Trim() != S.FinaleExtractionPoint || chkFinDouble.Checked != S.FinaleDoublePoints;
        }

        // false (with a warning) if something doesn't make sense
        bool ApplyFinaleControls()
        {
            string pt = tbFinPoint.Text.Trim();
            if (pt != "" && !Regex.IsMatch(pt, @"^\d+\s*[, ]\s*\d+$")) { Warn("The extraction point should look like 10600,9400 (x,y from the Map page), or be empty."); return false; }
            S.FinaleHordes = chkFinHordes.Checked;
            S.FinaleHordeLevel = cbFinLevel.SelectedIndex;
            S.FinaleStorm = chkFinStorm.Checked;
            S.FinaleChopper = chkFinChopper.Checked;
            S.FinaleExtraction = chkFinExtract.Checked;
            S.FinaleExtractionRadius = (int)nFinRadius.Value;
            S.FinaleExtractionPoint = Regex.Replace(pt, @"\s*[, ]\s*", ",");
            S.FinaleDoublePoints = chkFinDouble.Checked;
            return true;
        }
    }
}
