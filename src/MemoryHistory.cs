using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Ezpz
{
    // One bucket of the memory chart: the most memory in use after a cleanup during that minute (or 15 minutes).
    public class MemPoint
    {
        public long T;     // bucket start, Unix seconds (local clock)
        public long A;     // MB after any cleanup
        public long M;     // MB after a full (major) cleanup; 0 if none in this bucket
    }

    // A summary of one server run, kept so you can see whether memory needs grow over weeks.
    public class MemRun
    {
        public string Start = "", End = "";
        public long PeakMB, MaxMB;
        public int Stalls;
        public double StallMs;
        public bool OutOfMemory;
        public DateTime StartTime { get { return Parse(Start); } }
        public DateTime EndTime { get { return Parse(End); } }
        static DateTime Parse(string s) { DateTime d; return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue; }
    }

    // The server's memory over time, kept by ezpz across restarts (Java's own GC log starts again every run and
    // rotates on long runs). Size is fixed: 48 hours at one point per minute, 90 days at one point per 15 minutes,
    // and the last 200 runs. Kept in %APPDATA%\ezpz\servers\<id>.memory.json (a few hundred KB at most).
    public class MemoryHistory
    {
        public const int FineSeconds = 60, CoarseSeconds = 15 * 60;
        public const int FineHours = 48, CoarseDays = 90, MaxRuns = 200;

        public List<MemPoint> Fine = new List<MemPoint>();
        public List<MemPoint> Coarse = new List<MemPoint>();
        public List<MemRun> Runs = new List<MemRun>();

        [ScriptIgnore] public bool Dirty;

        static readonly DateTime Epoch = new DateTime(1970, 1, 1);
        public static long ToUnix(DateTime t) { return (long)(t - Epoch).TotalSeconds; }
        public static DateTime FromUnix(long s) { return Epoch.AddSeconds(s); }

        // Adding the same cleanup twice changes nothing (a bucket keeps its maximum), so re-reading a GC log is harmless.
        public void Add(DateTime t, long mb, bool major)
        {
            long now = ToUnix(DateTime.Now), u = ToUnix(t);
            if (u > now + 300) return;
            if (u >= now - FineHours * 3600) Merge(Fine, u - u % FineSeconds, mb, major);
            if (u >= now - CoarseDays * 86400L) Merge(Coarse, u - u % CoarseSeconds, mb, major);
        }

        void Merge(List<MemPoint> list, long bucket, long mb, bool major)
        {
            int i = list.Count - 1;
            if (i < 0 || list[i].T < bucket) { list.Add(new MemPoint { T = bucket, A = mb, M = major ? mb : 0 }); Dirty = true; return; }
            // usually the last bucket; older ones only when a log is read again
            int lo = 0, hi = list.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].T == bucket) { Bump(list[mid], mb, major); return; }
                if (list[mid].T < bucket) lo = mid + 1; else hi = mid - 1;
            }
            list.Insert(lo, new MemPoint { T = bucket, A = mb, M = major ? mb : 0 });
            Dirty = true;
        }

        void Bump(MemPoint p, long mb, bool major)
        {
            if (mb > p.A) { p.A = mb; Dirty = true; }
            if (major && mb > p.M) { p.M = mb; Dirty = true; }
        }

        // The current run's summary, from what the GC log says so far.
        public void UpdateRun(DateTime start, GcStats gc)
        {
            if (start == DateTime.MinValue || !gc.HasData) return;
            // the same run can be seen with slightly different start times (Java's log vs the process start)
            var r = Runs.LastOrDefault(x => Math.Abs((x.StartTime - start).TotalSeconds) < 120);
            if (r == null)
            {
                r = new MemRun { Start = start.ToString("s") };
                Runs.Add(r);
                Runs.Sort((a, b) => string.CompareOrdinal(a.Start, b.Start));
            }
            string end = (gc.LastGc > start ? gc.LastGc : DateTime.Now).ToString("s");
            if (gc.NeedMB > r.PeakMB || gc.MaxMB != r.MaxMB || gc.Stalls != r.Stalls || gc.OutOfMemory != r.OutOfMemory || end != r.End)
            {
                // a rotated log only holds part of the run, so keep the larger counts
                r.PeakMB = Math.Max(r.PeakMB, gc.NeedMB);
                r.MaxMB = gc.MaxMB > 0 ? gc.MaxMB : r.MaxMB;
                r.Stalls = Math.Max(r.Stalls, gc.Stalls);
                r.StallMs = Math.Max(r.StallMs, gc.StallMsTotal);
                r.OutOfMemory |= gc.OutOfMemory;
                r.End = end;
                Dirty = true;
            }
        }

        void Prune()
        {
            long now = ToUnix(DateTime.Now);
            Fine.RemoveAll(p => p.T < now - FineHours * 3600);
            Coarse.RemoveAll(p => p.T < now - CoarseDays * 86400L);
            if (Runs.Count > MaxRuns) Runs.RemoveRange(0, Runs.Count - MaxRuns);
        }

        // The points for a time range: one per minute up to 48 hours back, one per 15 minutes beyond that.
        public List<MemPoint> Range(DateTime from, DateTime to)
        {
            long a = ToUnix(from), b = ToUnix(to);
            var src = (DateTime.Now - from).TotalHours <= FineHours ? Fine : Coarse;
            return src.Where(p => p.T >= a && p.T <= b).ToList();
        }

        // ---------- file

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string PathFor(string id) { return Path.Combine(AppSettings.ManagerDir, "servers", id + ".memory.json"); }

        public static MemoryHistory Load(string id)
        {
            try
            {
                var p = PathFor(id);
                if (File.Exists(p)) return Json.Deserialize<MemoryHistory>(File.ReadAllText(p)) ?? new MemoryHistory();
            }
            catch (Exception ex) { AppLog.Error("reading the memory history", ex); }
            return new MemoryHistory();
        }

        public void Save(string id)
        {
            Prune();
            try
            {
                var p = PathFor(id);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p + ".tmp", Json.Serialize(this));
                if (File.Exists(p)) File.Replace(p + ".tmp", p, null); else File.Move(p + ".tmp", p);
                Dirty = false;
            }
            catch (Exception ex) { AppLog.Error("saving the memory history", ex); }
        }
    }
}
