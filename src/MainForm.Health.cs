using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ezpz
{
    // Health page: Java memory (from the GC log ezpz enables), PC resources, crash signs,
    // and live numbers from PZ's own "stats <group> all" command.
    public partial class MainForm
    {
        Page pgHealth;
        Label lblVerdict, lblVerdictDetail, lblHeapNums, lblPcNums, lblProblems, lblStatsNote;
        HeapChart heapChart;
        FlowLayoutPanel statTiles;
        readonly GcLogReader gcReader = new GcLogReader();
        TimeSpan lastCpu;
        DateTime lastCpuAt;
        double serverCpuPct = -1;
        DateTime statsCaptureUntil = DateTime.MinValue, lastStatsPoll = DateTime.MinValue;
        readonly Dictionary<string, double> liveStats = new Dictionary<string, double>();

        static readonly string[] StatGroups = { "performance", "game", "network", "connection" };
        static readonly Regex StatLine = new Regex(@"^\s*([a-z][a-z0-9\-]*):\s*(-?[\d.]+)\s*$");

        // Tile key -> caption. Keys are counter names from "stats <group> all" (plus two combined ones).
        static readonly string[][] Tiles =
        {
            new[] { "players", "Players online" },
            new[] { "fps", "Server FPS" },
            new[] { "zombies-loaded", "Zombies loaded" },
            new[] { "zombies-total", "Zombies in world" },
            new[] { "loaded-cells", "Map cells loaded" },
            new[] { "animals-objects", "Animals" },
            new[] { "net-in", "Network in" },
            new[] { "net-out", "Network out" },
            new[] { "packet-loss-last-second", "Packet loss" },
            new[] { "zombies-killed-today", "Zombie kills today" },
            new[] { "player-deaths-today", "Deaths today" },
            new[] { "memory-used", "Java memory now" },
        };

        void BuildHealth()
        {
            var p = pgHealth = NewPage();
            p.H1("Health");
            lblVerdict = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Theme.Text, MaximumSize = new Size(Page.Wide, 0) };
            p.Add(lblVerdict, 32);
            lblVerdictDetail = Fixed(p, 62, Theme.Text);

            p.H2("Live server");
            statTiles = new FlowLayoutPanel { Size = new Size(Page.Wide, 152), WrapContents = true, BackColor = Color.Transparent };
            foreach (var t in Tiles) statTiles.Controls.Add(MakeTile(t[1]));
            p.Add(statTiles, 148);
            lblStatsNote = Fixed(p, 24, Theme.Muted);

            p.H2("Memory in use (after each Java memory cleanup)");
            heapChart = new HeapChart { Size = new Size(Page.Wide, 210) };
            p.Add(heapChart, 210);
            lblHeapNums = Fixed(p, 44, Theme.Text);
            p.P("Windows shows the server holding most of its max all the time, because Java reserves memory up front. The line above is what the world actually needs, measured by Java.");

            p.H2("This PC");
            lblPcNums = Fixed(p, 44, Theme.Muted);

            p.H2("Problems");
            lblProblems = Fixed(p, 118, Theme.Muted);

            var raw = Theme.Btn("Show all server stats in the console");
            raw.Click += (s, e) => { foreach (var g in StatGroups) Cmd("stats " + g + " all"); ShowPage(PConsole); };
            p.Buttons(raw);
            p.End();
        }

        void RefreshHealth()
        {
            if (pgHealth == null) return;
            var gc = gcReader.Read(S.GcLogPath);
            bool running = server.State == ServerState.Running || server.State == ServerState.Starting;
            var ci = new Microsoft.VisualBasic.Devices.ComputerInfo();
            double totalGB = ci.TotalPhysicalMemory / (1024.0 * 1024 * 1024);
            double freeGB = ci.AvailablePhysicalMemory / (1024.0 * 1024 * 1024);

            var v = HealthCheck.Judge(S, gc, running, File.Exists(S.GcLogPath), totalGB);
            lblVerdict.Text = (v.Level == 0 ? "✓ " : v.Level == 3 ? "✕ " : v.Level == 2 ? "! " : "• ") + v.Title;
            lblVerdict.ForeColor = v.Level == 0 ? Theme.Accent : v.Level == 1 ? Theme.Info : v.Level == 2 ? Theme.Warn : Theme.Danger;
            lblVerdictDetail.Text = v.Detail;

            heapChart.SetData(gc.ChartPoints, gc.MaxMB > 0 ? gc.MaxMB : S.MaxRamGB * 1024L);
            if (gc.HasData)
            {
                string when = running ? "this run" : "the last run (ended " + FriendlyTime(gc.LastGc) + ")";
                lblHeapNums.Text = "Now: " + Gb(gc.LastAfterMB) + "   ·   Peak: " + Gb(gc.NeedMB) + "   ·   Max: " + Gb(gc.MaxMB > 0 ? gc.MaxMB : S.MaxRamGB * 1024L)
                    + "   ·   " + gc.Collections + " cleanups (they run alongside the game, not as pauses)"
                    + "   ·   Waits for memory: " + gc.Stalls + "   (" + when + ")";
            }
            else lblHeapNums.Text = "No memory data yet.";

            UpdateCpu();
            string proc = server.Pid != 0
                ? "Server process: " + Util.Size(server.MemoryBytes()) + " held in RAM" + (serverCpuPct >= 0 ? ", CPU " + serverCpuPct.ToString("0") + "%" : "")
                : "Server process: not running";
            lblPcNums.Text = "PC memory: " + totalGB.ToString("0.0") + " GB total, " + freeGB.ToString("0.0") + " GB free   ·   " + proc
                + "   ·   Memory settings: start " + S.MinRamGB + " GB, max " + S.MaxRamGB + " GB (Setup page)";
            lblPcNums.ForeColor = freeGB < 1.0 ? Theme.Warn : Theme.Muted;

            var problems = HealthCheck.FindProblems(S, gc, crashTimes.Count);
            lblProblems.Text = problems.Count == 0 ? "None found." : "• " + string.Join("\n• ", problems);
            lblProblems.ForeColor = problems.Count == 0 ? Theme.Muted : Theme.Warn;

            if (server.State == ServerState.Running) { if ((DateTime.Now - lastStatsPoll).TotalSeconds >= 10) PollLiveStats(); }
            else UpdateTiles(false);
        }

        // These labels change while the page is open, so they get a fixed box instead of growing over what's below.
        static Label Fixed(Page p, int height, Color color)
        {
            var l = new Label { AutoSize = false, Size = new Size(Page.Wide, height), ForeColor = color };
            p.Add(l, height - 4);
            return l;
        }

        static string Gb(long mb) { return (mb / 1024.0).ToString("0.0") + " GB"; }

        void UpdateCpu()
        {
            try
            {
                int pid = server.Pid;
                if (pid == 0) { serverCpuPct = -1; lastCpuAt = DateTime.MinValue; return; }
                var pr = Process.GetProcessById(pid);
                var cpu = pr.TotalProcessorTime;
                var now = DateTime.Now;
                if (lastCpuAt > DateTime.MinValue)
                    serverCpuPct = (cpu - lastCpu).TotalMilliseconds / (now - lastCpuAt).TotalMilliseconds / Environment.ProcessorCount * 100;
                lastCpu = cpu;
                lastCpuAt = now;
            }
            catch { serverCpuPct = -1; }
        }

        // ---------- live stats tiles

        Control MakeTile(string caption)
        {
            var t = new Panel { Size = new Size(140, 66), BackColor = Theme.Card, Margin = new Padding(0, 0, 8, 8) };
            var v = new Label { Text = "—", AutoSize = false, Location = new Point(10, 6), Size = new Size(126, 30), Font = new Font("Segoe UI Semibold", 15f), ForeColor = Theme.Text };
            var k = new Label { Text = caption, AutoSize = false, Location = new Point(10, 40), Size = new Size(126, 20), ForeColor = Theme.Muted };
            t.Controls.Add(v);
            t.Controls.Add(k);
            return t;
        }

        void PollLiveStats()
        {
            lastStatsPoll = DateTime.Now;
            statsCaptureUntil = DateTime.Now.AddSeconds(4);
            foreach (var g in StatGroups)
            {
                try { server.SendCommand("stats " + g + " all", false); }
                catch { return; }
            }
            System.Threading.Tasks.Task.Delay(2500).ContinueWith(t => Ui(() => UpdateTiles(true)));
        }

        // Called for every console line. Returns true for the manager's own stats replies, which stay out of the console.
        bool CaptureStats(string l)
        {
            if (DateTime.Now > statsCaptureUntil) return false;
            int gt = l.IndexOf("> ", StringComparison.Ordinal);
            string text = (l.StartsWith("LOG") || l.StartsWith("WARN")) && gt > 0 ? l.Substring(gt + 2) : l;
            if (text.Trim() == "" || text.StartsWith("Pool<")) return true;
            var m = StatLine.Match(text);
            if (!m.Success) return false;
            double v;
            if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                lock (liveStats) liveStats[m.Groups[1].Value] = v;
            return true;
        }

        void UpdateTiles(bool live)
        {
            if (statTiles == null) return;
            Dictionary<string, double> s;
            lock (liveStats) s = new Dictionary<string, double>(liveStats);
            Func<string, double> g = k => { double x; return s.TryGetValue(k, out x) ? x : double.NaN; };
            for (int i = 0; i < Tiles.Length; i++)
            {
                string key = Tiles[i][0], text;
                double v;
                if (key == "net-in") v = g("received-bps");
                else if (key == "net-out") v = g("sent-bps");
                else if (key == "player-deaths-today") v = g("players-killed-by-zombie-today") + g("players-killed-by-fire-today") + g("players-killed-by-player-today");
                else v = g(key);
                if (!live || double.IsNaN(v)) text = "—";
                else if (key == "net-in" || key == "net-out") text = Util.Size((long)v) + "/s";
                else if (key == "memory-used") text = (v / 1024).ToString("0.0") + " GB";
                else if (key == "packet-loss-last-second") text = v.ToString("0.#");
                else text = v.ToString("0");
                ((Label)statTiles.Controls[i].Controls[0]).Text = text;
            }
            lblStatsNote.Text = live && s.Count > 0
                ? "From the server's own stats command, updated " + DateTime.Now.ToString("HH:mm:ss") + ". Refreshes every 10 seconds while this page is open."
                : "Shown while the server is running.";
        }
    }

    // Single-series line chart: heap in use after each GC, with the max as a dashed reference line.
    class HeapChart : Control
    {
        static readonly Color Bg = Color.FromArgb(36, 37, 42), Grid = Color.FromArgb(58, 60, 66), Line = Theme.Accent, MaxLine = Theme.Muted;
        List<KeyValuePair<DateTime, long>> pts = new List<KeyValuePair<DateTime, long>>();
        long maxMB;
        int hover = -1;
        const int L = 56, R = 16, T = 14, B = 26;

        public HeapChart()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Bg;
            Font = new Font("Segoe UI", 8.5f);
        }

        public void SetData(List<KeyValuePair<DateTime, long>> data, long max)
        {
            pts = data.ToList();
            maxMB = Math.Max(1, max);
            Invalidate();
        }

        RectangleF Plot { get { return new RectangleF(L, T, Width - L - R, Height - T - B); } }

        PointF Map(int i)
        {
            var r = Plot;
            DateTime t0 = pts[0].Key, t1 = pts[pts.Count - 1].Key;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);
            float x = r.Left + (float)((pts[i].Key - t0).TotalSeconds / span) * r.Width;
            float y = r.Bottom - (float)(pts[i].Value / (double)maxMB) * r.Height;
            return new PointF(x, Math.Max(r.Top, y));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = Plot;
            using (var gridPen = new Pen(Grid, 1))
            using (var textBrush = new SolidBrush(Theme.Muted))
            {
                for (int q = 0; q <= 3; q++)   // gridlines at 0/25/50/75% of max, labelled in GB
                {
                    float y = r.Bottom - r.Height * q / 4f;
                    g.DrawLine(gridPen, r.Left, y, r.Right, y);
                    string lab = (maxMB * q / 4 / 1024.0).ToString("0.#") + " GB";
                    var sz = g.MeasureString(lab, Font);
                    g.DrawString(lab, Font, textBrush, r.Left - sz.Width - 6, y - sz.Height / 2);
                }
                using (var maxPen = new Pen(MaxLine, 1.5f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(maxPen, r.Left, r.Top, r.Right, r.Top);
                string maxLab = "Max " + (maxMB / 1024.0).ToString("0.#") + " GB";
                var ms = g.MeasureString(maxLab, Font);
                g.DrawString(maxLab, Font, textBrush, r.Left - ms.Width - 6, r.Top - ms.Height / 2);

                if (pts.Count == 0)
                {
                    const string msg = "No memory data yet";
                    var s = g.MeasureString(msg, Font);
                    g.DrawString(msg, Font, textBrush, r.Left + (r.Width - s.Width) / 2, r.Top + (r.Height - s.Height) / 2);
                    return;
                }
                g.DrawString(pts[0].Key.ToString("HH:mm"), Font, textBrush, r.Left, r.Bottom + 5);
                string end = pts[pts.Count - 1].Key.ToString("HH:mm");
                g.DrawString(end, Font, textBrush, r.Right - g.MeasureString(end, Font).Width, r.Bottom + 5);
            }

            var line = Enumerable.Range(0, pts.Count).Select(Map).ToArray();
            using (var pen = new Pen(Line, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (line.Length == 1) { using (var b = new SolidBrush(Line)) g.FillEllipse(b, line[0].X - 4, line[0].Y - 4, 8, 8); }
                else g.DrawLines(pen, line);
            }

            if (hover >= 0 && hover < pts.Count)
            {
                var p = line[hover];
                using (var cross = new Pen(Color.FromArgb(120, Theme.Text), 1))
                    g.DrawLine(cross, p.X, r.Top, p.X, r.Bottom);
                using (var ring = new SolidBrush(Bg)) g.FillEllipse(ring, p.X - 6, p.Y - 6, 12, 12);
                using (var dot = new SolidBrush(Line)) g.FillEllipse(dot, p.X - 4, p.Y - 4, 8, 8);
                string tip = pts[hover].Key.ToString("HH:mm:ss") + "   " + (pts[hover].Value / 1024.0).ToString("0.00") + " GB in use";
                var ts = g.MeasureString(tip, Font);
                float tx = Math.Min(p.X + 10, r.Right - ts.Width - 8), ty = Math.Max(r.Top, p.Y - ts.Height - 14);
                using (var bg = new SolidBrush(Color.FromArgb(240, 20, 21, 24))) g.FillRectangle(bg, tx - 6, ty - 3, ts.Width + 12, ts.Height + 6);
                using (var fg = new SolidBrush(Theme.Text)) g.DrawString(tip, Font, fg, tx, ty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pts.Count == 0) return;
            int best = -1;
            float bestDx = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = Math.Abs(Map(i).X - e.X);
                if (dx < bestDx) { bestDx = dx; best = i; }
            }
            if (best != hover) { hover = best; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }
    }
}
