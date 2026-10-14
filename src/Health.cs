using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ezpz
{
    // What the JVM's GC log says about memory, for one server run.
    public class GcStats
    {
        public long MaxMB;
        public DateTime RunStart = DateTime.MinValue;
        public DateTime LastGc = DateTime.MinValue;
        public long LastAfterMB, PeakAfterMB, PeakMajorAfterMB;
        // A "stall" is one pause where game threads waited for memory. Java logs one line per waiting thread,
        // so lines within 100 ms are one pause. Pauses in the first 3 minutes (world loading) are counted apart:
        // the loader allocates in a burst on startup, which is normal and not a sign the max is too small.
        public int Collections, MajorCollections, Stalls, StartupStalls;
        public double LongestGcSec, StallMsTotal, StartupStallMs;
        internal DateTime lastStallAt = DateTime.MinValue;
        internal double lastStallMs;
        internal bool lastStallStartup;
        public bool OutOfMemory;
        public readonly List<KeyValuePair<DateTime, long>> Points = new List<KeyValuePair<DateTime, long>>();
        public readonly List<KeyValuePair<DateTime, long>> MajorPoints = new List<KeyValuePair<DateTime, long>>();
        // Minor (young-only) cleanups still count old garbage, so the chart prefers full cleanups once there are a few.
        public List<KeyValuePair<DateTime, long>> ChartPoints { get { return MajorPoints.Count >= 3 ? MajorPoints : Points; } }

        public bool HasData { get { return Collections > 0; } }
        // Live data after full (major) collections is the honest "how much does the world need" number.
        public long NeedMB { get { return PeakMajorAfterMB > 0 ? PeakMajorAfterMB : PeakAfterMB; } }
    }

    // Incrementally reads the GC log written by -Xlog:gc,gc+init (format checked against the Zulu 25 JRE PZ ships).
    public class GcLogReader
    {
        static readonly Regex Collection = new Regex(@"GC\(\d+\) (\w+) Collection \(([^)]*)\) (\d+)M\(\d+%\)->(\d+)M\(\d+%\) ([\d.]+)s");
        static readonly Regex Stall = new Regex(@"Allocation Stall \(([^)]*)\) ([\d.]+)ms");
        static readonly Regex MaxCap = new Regex(@"Max Capacity: (\d+)M");
        static readonly Regex Uptime = new Regex(@"\]\[([\d.]+)s\]");
        const double StartupSeconds = 180;
        const int MaxPoints = 1500;

        string path;
        long pos;
        string partial = "";
        public GcStats Stats = new GcStats();

        public GcStats Read(string file)
        {
            if (file != path) { path = file; Reset(); }
            if (!File.Exists(file)) { Reset(); return Stats; }
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < pos) Reset();   // rotated: a new server run
                    if (fs.Length == pos) return Stats;
                    fs.Seek(pos, SeekOrigin.Begin);
                    string text = partial + new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                    pos = fs.Length;
                    int lastNl = text.LastIndexOf('\n');
                    partial = lastNl < text.Length - 1 ? text.Substring(lastNl + 1) : "";
                    foreach (var line in text.Substring(0, lastNl + 1).Split('\n')) Parse(line.TrimEnd('\r'));
                }
            }
            catch (IOException) { }
            return Stats;
        }

        void Reset() { pos = 0; partial = ""; Stats = new GcStats(); }

        void Parse(string l)
        {
            if (l.Length == 0) return;
            if (l.Contains("Initializing The Z Garbage Collector")) { Stats = new GcStats(); Stats.RunStart = Stamp(l); }
            var m = MaxCap.Match(l);
            if (m.Success) { Stats.MaxMB = long.Parse(m.Groups[1].Value); return; }
            m = Collection.Match(l);
            if (m.Success)
            {
                long after = long.Parse(m.Groups[4].Value);
                double dur = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
                var t = Stamp(l);
                Stats.Collections++;
                Stats.LastAfterMB = after;
                Stats.LastGc = t;
                if (after > Stats.PeakAfterMB) Stats.PeakAfterMB = after;
                if (m.Groups[1].Value == "Major")
                {
                    Stats.MajorCollections++;
                    if (after > Stats.PeakMajorAfterMB) Stats.PeakMajorAfterMB = after;
                    Stats.MajorPoints.Add(new KeyValuePair<DateTime, long>(t, after));
                }
                if (dur > Stats.LongestGcSec) Stats.LongestGcSec = dur;
                Stats.Points.Add(new KeyValuePair<DateTime, long>(t, after));
                if (Stats.Points.Count > MaxPoints)   // keep it light: drop every other old point
                    for (int i = 0; i < Stats.Points.Count / 2; i++) Stats.Points.RemoveAt(i);
                return;
            }
            m = Stall.Match(l);
            if (m.Success)
            {
                double ms = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var t = Stamp(l);
                var um = Uptime.Match(l);
                bool startup = um.Success && double.Parse(um.Groups[1].Value, CultureInfo.InvariantCulture) < StartupSeconds;
                var s = Stats;
                if (s.lastStallAt != DateTime.MinValue && (t - s.lastStallAt).TotalMilliseconds <= 100 && s.lastStallStartup == startup)
                {
                    // another thread waiting in the same pause: the pause is as long as its longest wait
                    if (ms > s.lastStallMs)
                    {
                        if (startup) s.StartupStallMs += ms - s.lastStallMs; else s.StallMsTotal += ms - s.lastStallMs;
                        s.lastStallMs = ms;
                    }
                }
                else
                {
                    if (startup) { s.StartupStalls++; s.StartupStallMs += ms; }
                    else { s.Stalls++; s.StallMsTotal += ms; }
                    s.lastStallMs = ms;
                }
                s.lastStallAt = t;
                s.lastStallStartup = startup;
                return;
            }
            if (l.Contains("Out Of Memory")) Stats.OutOfMemory = true;
        }

        static DateTime Stamp(string l)
        {
            // [2026-10-13T23:49:51.344-0400][0.105s]...
            DateTime t;
            if (l.Length > 24 && DateTime.TryParseExact(l.Substring(1, 23), "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return DateTime.Now;
        }
    }

    public class HealthVerdict
    {
        public string Title, Detail;
        public int Level;   // 0 good, 1 info, 2 warning, 3 problem
    }

    public static class HealthCheck
    {
        public static List<string> FindProblems(AppSettings s, GcStats gc, int crashesThisSession)
        {
            var p = new List<string>();
            try
            {
                if (Directory.Exists(s.InstallDir))
                    foreach (var f in new DirectoryInfo(s.InstallDir).GetFiles("hs_err_pid*.log").OrderByDescending(f => f.LastWriteTime).Take(5))
                        p.Add("Java crashed on " + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + ". Crash report: " + f.FullName);
            }
            catch { }
            try
            {
                if (File.Exists(s.ConsoleLogPath) && GameData.ReadShared(s.ConsoleLogPath).Contains("OutOfMemoryError"))
                    p.Add("The server ran out of memory (OutOfMemoryError in this run's console log).");
            }
            catch { }
            if (gc.OutOfMemory) p.Add("Java reported running out of memory in the memory log.");
            if (gc.Stalls > 0) p.Add("While running, the game paused " + gc.Stalls + " time(s) waiting for memory (" + gc.StallMsTotal.ToString("0") + " ms in total). Players feel this as lag.");
            if (crashesThisSession > 0) p.Add("The server stopped unexpectedly " + crashesThisSession + " time(s) since ezpz was opened.");
            return p;
        }

        public static HealthVerdict Judge(AppSettings s, GcStats gc, bool running, bool logExists, double pcTotalGB)
        {
            int maxSetGB = s.MaxRamGB;
            var v = new HealthVerdict();
            string pcNote = "";
            if (maxSetGB > pcTotalGB - 3)
                pcNote = " Also: the max (" + maxSetGB + " GB) leaves under 3 GB of this PC's " + pcTotalGB.ToString("0") + " GB for Windows. Consider lowering it on the Setup page.";

            if (!gc.HasData)
            {
                v.Level = 1;
                if (running && !logExists) { v.Title = "Restart to turn on memory monitoring"; v.Detail = "ezpz adds memory logging when it starts the server. Restart the server once from ezpz and this page fills in."; }
                else if (running) { v.Title = "Collecting data…"; v.Detail = "Memory details appear after Java's first memory cleanup, usually within a minute or two of starting."; }
                else { v.Title = "Start the server to see memory health"; v.Detail = "ezpz logs memory use while the server runs and shows it here."; }
                v.Detail += pcNote;
                return v;
            }

            double maxGB = (gc.MaxMB > 0 ? gc.MaxMB : maxSetGB * 1024) / 1024.0;
            double needGB = gc.NeedMB / 1024.0;
            double pct = needGB / maxGB;
            int canGive = (int)Math.Floor(pcTotalGB - 4);
            double hours = gc.RunStart > DateTime.MinValue ? (gc.LastGc - gc.RunStart).TotalHours : 0;

            if (gc.OutOfMemory || gc.Stalls >= 3 || gc.StallMsTotal >= 1000)
            {
                v.Level = 3;
                v.Title = "Too small: the server ran short of memory";
                int target = Math.Min(maxSetGB + 2, canGive);
                v.Detail = target > maxSetGB
                    ? "Raise the max memory on the Setup page from " + maxSetGB + " GB to " + target + " GB, then restart."
                    : "This PC has no spare memory to give the server. Fewer or lighter mods, fewer players, or more RAM would help.";
            }
            else if (gc.Stalls > 0)
            {
                v.Level = 2;
                v.Title = "A brief wait for memory during play";
                v.Detail = "The game paused " + gc.Stalls + " time(s) (" + gc.StallMsTotal.ToString("0") + " ms) waiting for memory. Once in a while is OK; if it keeps happening, raise the max"
                    + (canGive > maxSetGB ? " to " + Math.Min(maxSetGB + 2, canGive) + " GB on the Setup page." : ", though this PC has no spare memory to give.");
            }
            else if (pct >= 0.85)
            {
                v.Level = 2;
                v.Title = "Tight: the world needs " + needGB.ToString("0.0") + " of " + maxGB.ToString("0") + " GB";
                v.Detail = canGive > maxSetGB
                    ? "No pauses yet, but there's little headroom. Raising the max to " + Math.Min(maxSetGB + 2, canGive) + " GB on the Setup page would be safer."
                    : "No pauses yet, but there's little headroom, and this PC has no spare memory to give.";
            }
            else if (pct < 0.35 && hours >= 0.5 && maxSetGB >= 6)
            {
                v.Level = 1;
                int lower = Math.Max(4, (int)Math.Ceiling(needGB * 2));
                v.Title = "Plenty: the world needs " + needGB.ToString("0.0") + " of " + maxGB.ToString("0") + " GB";
                v.Detail = lower < maxSetGB
                    ? "Everything is fine. If this PC needs memory for other things, you could lower the max to about " + lower + " GB and still have double what the world has needed."
                    : "Everything is fine.";
            }
            else
            {
                v.Level = 0;
                v.Title = "Memory looks right";
                v.Detail = "The world has needed up to " + needGB.ToString("0.0") + " GB of the " + maxGB.ToString("0") + " GB max, with no pauses waiting for memory.";
            }
            if (hours < 0.5 && v.Level < 3) v.Detail += " (Early days: this run is " + Math.Max(1, (int)(hours * 60)) + " min old. The verdict firms up as players explore.)";
            v.Detail += pcNote;
            return v;
        }
    }
}
