using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Ezpz
{
    // Reads PZ's built-in Prometheus metrics server (started with -DprometheusPort=<port>).
    // Unlike the "stats" console command, this leaves nothing in the server's logs.
    public static class Metrics
    {
        // name{labels} value
        static readonly Regex Line = new Regex(@"^([a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{([^}]*)\})?\s+(\S+)");
        static readonly Regex Label = new Regex("(\\w+)=\"((?:[^\"\\\\]|\\\\.)*)\"");

        public class Sample
        {
            public string Name;
            public Dictionary<string, string> Labels = new Dictionary<string, string>();
            public double Value;
        }

        public static List<Sample> Fetch(int port)
        {
            string body;
            using (var wc = new WebClient())
                body = wc.DownloadString("http://127.0.0.1:" + port + "/metrics");
            var list = new List<Sample>();
            foreach (var raw in body.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                var m = Line.Match(l);
                double v;
                if (!m.Success || !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                var s = new Sample { Name = m.Groups[1].Value, Value = v };
                foreach (Match lb in Label.Matches(m.Groups[2].Value)) s.Labels[lb.Groups[1].Value] = lb.Groups[2].Value;
                list.Add(s);
            }
            return list;
        }

        // Flattens into the same keys the "stats <group> all" command uses (e.g. "players", "fps"),
        // plus a few Java ones prefixed "jvm:".
        public static Dictionary<string, double> ToStats(List<Sample> samples)
        {
            var d = new Dictionary<string, double>();
            foreach (var s in samples)
            {
                string p;
                if ((s.Name == "game" || s.Name == "performance" || s.Name == "network" || s.Name == "connection")
                    && s.Labels.TryGetValue("parameter", out p) && !p.StartsWith("Pool<"))
                    d[p] = s.Value;
                else if (s.Name == "jvm_threads_deadlocked") d["jvm:deadlocked"] = s.Value;
                else if (s.Name == "jvm_threads_current") d["jvm:threads"] = s.Value;
                else if (s.Name == "process_cpu_seconds_total") d["jvm:cpu-seconds"] = s.Value;
                else if (s.Name == "jvm_gc_collection_seconds_sum" && s.Labels.ContainsKey("gc") && s.Labels["gc"].EndsWith("Pauses"))
                    d["jvm:gc-pause-seconds"] = (d.ContainsKey("jvm:gc-pause-seconds") ? d["jvm:gc-pause-seconds"] : 0) + s.Value;
            }
            return d;
        }
    }
}
