using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace Ezpz
{
    // A top-down map you can drag and zoom, drawn from the map image "pyramids" that ship with the server
    // (media\maps\<map>\pyramid.zip): 256 px tiles at zoom levels 0-4, where level 0 is one pixel per game tile,
    // so world coordinates are pixel coordinates. Markers (players) are drawn on top.
    public class MapView : Control
    {
        public class Marker
        {
            public string Id, Label;
            public double X, Y;
            public Color Color;
            public bool Hollow;   // last known position rather than live
            public string Tip = "";
        }

        class Layer
        {
            public ZipArchive Zip;
            public int X0, Y0, W, H, MaxLevel;
            public Dictionary<string, ZipArchiveEntry> Entries;
        }

        readonly List<Layer> layers = new List<Layer>();
        readonly Dictionary<string, Image> cache = new Dictionary<string, Image>();
        readonly LinkedList<string> lru = new LinkedList<string>();
        const int CacheMax = 400;

        public List<Marker> Markers = new List<Marker>();
        public Marker Selected;
        public event Action<Marker> MarkerClicked;
        public event Action<double, double> RightClickedAt;

        double cx = 10600, cy = 9700;   // view centre in world tiles (Muldraugh)
        double scale = 0.25;            // screen pixels per world tile
        Point dragFrom; bool dragging, dragged; double dragCx, dragCy;
        Point mouse = new Point(-1, -1);
        readonly ToolTip tip = new ToolTip();
        Marker hover;

        public string Problem;   // shown when there's no map to draw
        public bool HasMap { get { return layers.Count > 0; } }

        public MapView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(20, 22, 24);
            Cursor = Cursors.Hand;
        }

        // Loads the map images from the server install (and any map mods that ship their own).
        public void Load(IEnumerable<string> pyramidZips)
        {
            Unload();
            foreach (var path in pyramidZips)
            {
                try
                {
                    var zip = ZipFile.OpenRead(path);
                    var l = new Layer { Zip = zip, Entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase) };
                    ZipArchiveEntry meta;
                    if (l.Entries.TryGetValue("pyramid.txt", out meta))
                        using (var r = new StreamReader(meta.Open()))
                            foreach (var line in r.ReadToEnd().Split('\n'))
                                if (line.StartsWith("bounds="))
                                {
                                    var b = line.Substring(7).Trim().Split(' ').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                                    l.X0 = b[0]; l.Y0 = b[1]; l.W = b[2] - b[0]; l.H = b[3] - b[1];
                                }
                    l.MaxLevel = Enumerable.Range(0, 10).Where(i => l.Entries.ContainsKey(i + "/")).DefaultIfEmpty(0).Max();
                    layers.Add(l);
                }
                catch (Exception ex) { AppLog.Error("loading map images from " + path, ex); }
            }
            Invalidate();
        }

        public void Unload()
        {
            foreach (var l in layers) try { l.Zip.Dispose(); } catch { }
            layers.Clear();
            foreach (var img in cache.Values) if (img != null) img.Dispose();   // null = no image for that spot
            cache.Clear(); lru.Clear();
        }

        protected override void Dispose(bool disposing) { if (disposing) Unload(); base.Dispose(disposing); }

        public void CenterOn(double x, double y, double? newScale = null)
        {
            cx = x; cy = y;
            if (newScale.HasValue) scale = Clamp(newScale.Value);
            Invalidate();
        }

        static double Clamp(double s) { return Math.Max(1.0 / 64, Math.Min(4, s)); }

        PointF ToScreen(double x, double y) { return new PointF((float)((x - cx) * scale + Width / 2.0), (float)((y - cy) * scale + Height / 2.0)); }
        void ToWorld(Point p, out double x, out double y) { x = (p.X - Width / 2.0) / scale + cx; y = (p.Y - Height / 2.0) / scale + cy; }

        Image Tile(Layer l, int level, int tx, int ty)
        {
            string key = l.GetHashCode() + "/" + level + "/" + tx + "x" + ty;
            Image img;
            if (cache.TryGetValue(key, out img)) { lru.Remove(key); lru.AddFirst(key); return img; }
            ZipArchiveEntry e;
            if (!l.Entries.TryGetValue(level + "/tile" + tx + "x" + ty + ".png", out e)) { cache[key] = null; lru.AddFirst(key); return null; }
            try
            {
                using (var s = e.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    ms.Position = 0;
                    img = new Bitmap(Image.FromStream(ms));
                }
            }
            catch { img = null; }
            cache[key] = img;
            lru.AddFirst(key);
            while (lru.Count > CacheMax)
            {
                var old = lru.Last.Value;
                lru.RemoveLast();
                Image gone;
                if (cache.TryGetValue(old, out gone) && gone != null) gone.Dispose();
                cache.Remove(old);
            }
            return img;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (layers.Count == 0)
            {
                TextRenderer.DrawText(g, Problem ?? "No map loaded.", Theme.Base, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            g.InterpolationMode = scale > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var l in layers)
            {
                // the coarsest level whose pixels are still at least as fine as the screen's
                int level = Math.Max(0, Math.Min(l.MaxLevel, (int)Math.Floor(Math.Log(1 / scale, 2))));
                int span = 256 << level;   // world tiles per image tile
                double x0, y0, x1, y1;
                ToWorld(Point.Empty, out x0, out y0);
                ToWorld(new Point(Width, Height), out x1, out y1);
                int tx0 = Math.Max(0, (int)Math.Floor((x0 - l.X0) / span)), ty0 = Math.Max(0, (int)Math.Floor((y0 - l.Y0) / span));
                int tx1 = (int)Math.Floor((x1 - l.X0) / span), ty1 = (int)Math.Floor((y1 - l.Y0) / span);
                for (int ty = ty0; ty <= ty1; ty++)
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        var img = Tile(l, level, tx, ty);
                        if (img == null) continue;
                        var a = ToScreen(l.X0 + tx * span, l.Y0 + ty * span);
                        var b = ToScreen(l.X0 + (tx + 1) * span, l.Y0 + (ty + 1) * span);
                        g.DrawImage(img, new RectangleF(a.X, a.Y, b.X - a.X + 0.5f, b.Y - a.Y + 0.5f));
                    }
            }

            // markers
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (var font = new Font("Segoe UI Semibold", 9f))
            using (var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
            {
                foreach (var m in Markers.OrderBy(x => x == Selected ? 2 : x.Hollow ? 0 : 1))   // live players over last-saved rings, the selected one on top
                {
                    var p = ToScreen(m.X, m.Y);
                    float r = m == Selected ? 8 : 6;
                    using (var fill = new SolidBrush(m.Color))
                    using (var ring = new Pen(m == Selected ? Color.White : Color.FromArgb(220, 0, 0, 0), m == Selected ? 3 : 2))
                    {
                        if (m.Hollow) g.FillEllipse(shadow, p.X - r, p.Y - r, 2 * r, 2 * r);
                        else g.FillEllipse(fill, p.X - r, p.Y - r, 2 * r, 2 * r);
                        if (m.Hollow) using (var hp = new Pen(m.Color, 3)) g.DrawEllipse(hp, p.X - r + 1, p.Y - r + 1, 2 * r - 2, 2 * r - 2);
                        else g.DrawEllipse(ring, p.X - r, p.Y - r, 2 * r, 2 * r);
                    }
                    var sz = g.MeasureString(m.Label, font);
                    var lr = new RectangleF(p.X + r + 3, p.Y - sz.Height / 2, sz.Width + 6, sz.Height);
                    g.FillRectangle(shadow, lr);
                    using (var tb = new SolidBrush(Color.White)) g.DrawString(m.Label, font, tb, lr.X + 3, lr.Y);
                }
            }

            // scale and cursor position
            double mx, my;
            string where = "";
            if (mouse.X >= 0) { ToWorld(mouse, out mx, out my); where = "  ·  " + (int)mx + ", " + (int)my; }
            string zoom = scale >= 1 ? "zoom ×" + scale.ToString("0.#") : "zoom 1:" + (1 / scale).ToString("0");
            var info = zoom + where;
            var isz = TextRenderer.MeasureText(info, Theme.Base);
            var ir = new Rectangle(8, Height - isz.Height - 12, isz.Width + 12, isz.Height + 6);
            using (var bg = new SolidBrush(Color.FromArgb(190, 20, 22, 24))) g.FillRectangle(bg, ir);
            TextRenderer.DrawText(g, info, Theme.Base, new Point(ir.X + 6, ir.Y + 3), Theme.Text);
        }

        Marker HitTest(Point p)
        {
            foreach (var m in Markers.OrderBy(x => x == Selected ? 2 : x.Hollow ? 0 : 1).Reverse())   // the topmost marker wins
            {
                var s = ToScreen(m.X, m.Y);
                if (Math.Abs(s.X - p.X) <= 9 && Math.Abs(s.Y - p.Y) <= 9) return m;
            }
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left) { dragging = true; dragged = false; dragFrom = e.Location; dragCx = cx; dragCy = cy; Cursor = Cursors.SizeAll; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            if (dragging)
            {
                if (Math.Abs(e.X - dragFrom.X) + Math.Abs(e.Y - dragFrom.Y) > 3) dragged = true;
                cx = dragCx - (e.X - dragFrom.X) / scale;
                cy = dragCy - (e.Y - dragFrom.Y) / scale;
            }
            else
            {
                var h = HitTest(e.Location);
                if (h != hover) { hover = h; if (h != null) tip.Show(h.Tip, this, e.X + 14, e.Y + 14); else tip.Hide(this); }
            }
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
                Cursor = Cursors.Hand;
                if (!dragged)
                {
                    var m = HitTest(e.Location);
                    Selected = m;
                    var h = MarkerClicked;
                    if (h != null) h(m);
                    Invalidate();
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                double x, y;
                ToWorld(e.Location, out x, out y);
                var h = RightClickedAt;
                if (h != null) h(x, y);
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); tip.Hide(this); hover = null; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ZoomAt(e.Location, 2);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomAt(e.Location, e.Delta > 0 ? 1.25 : 0.8);
            base.OnMouseWheel(e);
        }

        // zoom keeping the world point under the cursor in place
        public void ZoomAt(Point p, double factor)
        {
            double wx, wy;
            ToWorld(p, out wx, out wy);
            scale = Clamp(scale * factor);
            cx = wx - (p.X - Width / 2.0) / scale;
            cy = wy - (p.Y - Height / 2.0) / scale;
            Invalidate();
        }

        public void Zoom(double factor) { ZoomAt(new Point(Width / 2, Height / 2), factor); }
    }
}
