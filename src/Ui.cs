using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Ezpz
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 25, 28);
        public static readonly Color Side = Color.FromArgb(32, 34, 37);
        public static readonly Color Card = Color.FromArgb(48, 50, 55);
        public static readonly Color Input = Color.FromArgb(36, 37, 41);
        public static readonly Color Text = Color.FromArgb(228, 230, 235);
        public static readonly Color Muted = Color.FromArgb(150, 155, 163);
        public static readonly Color Accent = Color.FromArgb(120, 200, 100);
        public static readonly Color AccentBtn = Color.FromArgb(62, 128, 55);
        public static readonly Color Danger = Color.FromArgb(235, 95, 85);
        public static readonly Color DangerBtn = Color.FromArgb(170, 48, 42);
        public static readonly Color Warn = Color.FromArgb(222, 178, 60);
        public static readonly Color Info = Color.FromArgb(110, 168, 235);

        public static readonly Font Base = new Font("Segoe UI", 9.75f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font H1 = new Font("Segoe UI Semibold", 17f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 11.5f);
        public static readonly Font Mono = new Font("Consolas", 9.75f);

        public static Button Btn(string text) { return Btn(text, Card); }

        public static Button Btn(string text, Color back)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Font = Bold;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(10, 5, 10, 5);
            b.Margin = new Padding(0, 0, 8, 8);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static TextBox Box(string text)
        {
            var t = new TextBox();
            t.Text = text ?? "";
            t.BackColor = Input;
            t.ForeColor = Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        public static CheckBox Check(string text, bool value)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Checked = value;
            c.AutoSize = true;
            c.ForeColor = Text;
            return c;
        }

        public static NumericUpDown Num(int min, int max, int value)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.BackColor = Input;
            n.ForeColor = Text;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }
    }

    // Small themed text prompt. Returns null if cancelled.
    static class Prompt
    {
        public static string Ask(IWin32Window owner, string title, string label, string value, bool password)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = Theme.Bg; f.ForeColor = Theme.Text; f.Font = Theme.Base;
                f.AutoScaleDimensions = new SizeF(96f, 96f);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(440, 140);
                var l = new Label { Text = label, Location = new Point(16, 14), AutoSize = true, MaximumSize = new Size(408, 0), UseMnemonic = false };
                var tb = Theme.Box(value);
                tb.Location = new Point(16, 60); tb.Width = 408;
                tb.UseSystemPasswordChar = password;
                var ok = Theme.Btn("OK", Theme.AccentBtn);
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(270, 96);
                var cancel = Theme.Btn("Cancel");
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(350, 96);
                f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }
    }

    // A scrollable page that stacks controls top-to-bottom.
    class Page : Panel
    {
        public int Y = 22;
        public const int X = 28;
        public const int LabelW = 220;
        public const int Wide = 900;

        public Page()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Visible = false;
        }

        public Label H1(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.H1; l.AutoSize = true; l.ForeColor = Theme.Text;
            l.Location = new Point(X, Y);
            Controls.Add(l);
            Y += 44;
            return l;
        }

        public Label H2(string t)
        {
            Y += 12;
            var l = new Label();
            l.Text = t; l.Font = Theme.H2; l.AutoSize = true; l.ForeColor = Theme.Accent;
            l.Location = new Point(X, Y);
            Controls.Add(l);
            Y += 30;
            return l;
        }

        public Label P(string t) { return P(t, Theme.Muted); }

        public Label P(string t, Color c)
        {
            var l = new Label();
            l.AutoSize = true;
            l.MaximumSize = new Size(Wide, 0);
            l.Text = t;
            l.ForeColor = c;
            l.Location = new Point(X, Y);
            Controls.Add(l);
            Y += l.GetPreferredSize(new Size(Wide, 0)).Height + 8;
            return l;
        }

        public T Row<T>(string label, T c, int w) where T : Control
        {
            var l = new Label();
            l.Text = label; l.AutoSize = false; l.Size = new Size(LabelW - 8, 24);
            l.ForeColor = Theme.Text; l.Location = new Point(X, Y + 3);
            Controls.Add(l);
            c.Location = new Point(X + LabelW, Y);
            if (w > 0) c.Width = w;
            Controls.Add(c);
            Y += Math.Max(c.Height, 24) + 8;
            return c;
        }

        // Muted helper text under the previous row, aligned with the input column.
        public void Hint(string t)
        {
            var l = new Label();
            l.AutoSize = true; l.MaximumSize = new Size(Wide - LabelW, 0);
            l.Text = t; l.ForeColor = Theme.Muted;
            l.Location = new Point(X + LabelW, Y - 6);
            Controls.Add(l);
            Y += l.GetPreferredSize(new Size(Wide - LabelW, 0)).Height + 4;
        }

        public TextBox PathRow(string label, string value, Action<TextBox> browse)
        {
            int y = Y;
            var tb = Row(label, Theme.Box(value), 520);
            var b = Theme.Btn("Browse…");
            b.AutoSize = false;
            b.Padding = new Padding(0);
            b.Margin = new Padding(0);
            b.Size = new Size(90, tb.Height);
            b.Location = new Point(X + LabelW + 528, y);
            b.Click += (s, e) => browse(tb);
            Controls.Add(b);
            return tb;
        }

        public FlowLayoutPanel Buttons(params Control[] bs)
        {
            var f = new FlowLayoutPanel();
            f.Location = new Point(X, Y);
            f.AutoSize = true;
            f.MaximumSize = new Size(Wide, 0);
            f.WrapContents = true;
            f.BackColor = Color.Transparent;
            f.Controls.AddRange(bs);
            Controls.Add(f);
            Y += f.GetPreferredSize(new Size(Wide, 0)).Height + 8;
            return f;
        }

        public void Add(Control c, int height)
        {
            c.Location = new Point(X, Y);
            Controls.Add(c);
            Y += height + 8;
        }

        public void End()
        {
            var l = new Label();
            l.Size = new Size(1, 30);
            l.Location = new Point(X, Y);
            Controls.Add(l);
        }
    }

    // A text box that suggests catalog entries (items or vehicles) as you type: words can match the name or
    // the id, Up/Down and Enter pick, Escape closes. The box always holds a plain id, so typing one still works.
    class IdPicker
    {
        public readonly TextBox Box;
        public Func<List<CatalogEntry>> Source = () => new List<CatalogEntry>();
        public event Action Submit;    // Enter with no suggestion list open
        ListBox list;
        bool setting;

        public IdPicker(string text)
        {
            Box = Theme.Box(text);
            Box.TextChanged += (s, e) => { if (!setting) Suggest(); };
            Box.KeyDown += OnKey;
            Box.Leave += (s, e) => Box.BeginInvoke(new Action(() => { if (list != null && !list.Focused) Hide(); }));
            Box.MouseWheel += (s, e) => Hide();
        }

        public void Hide() { if (list != null) list.Visible = false; }

        void OnKey(object s, KeyEventArgs e)
        {
            bool open = list != null && list.Visible && list.Items.Count > 0;
            if (e.KeyCode == Keys.Down && open) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up && open) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && open) { Hide(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (open && list.SelectedIndex >= 0) Choose((CatalogEntry)list.SelectedItem);
                else if (Submit != null) Submit();
            }
        }

        void Choose(CatalogEntry c)
        {
            setting = true;
            Box.Text = c.Id;
            Box.SelectionStart = Box.Text.Length;
            setting = false;
            Hide();
            Box.Focus();
        }

        void Suggest()
        {
            var form = Box.FindForm();
            if (form == null) return;
            var found = ItemCatalog.Search(Source(), Box.Text, 40);
            if (found.Count == 0 || (found.Count == 1 && string.Equals(found[0].Id, Box.Text.Trim(), StringComparison.OrdinalIgnoreCase))) { Hide(); return; }
            if (list == null)
            {
                list = new ListBox
                {
                    DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24, BackColor = Theme.Card, ForeColor = Theme.Text,
                    BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, TabStop = false, Visible = false, Font = Theme.Base
                };
                list.DrawItem += DrawEntry;
                list.MouseClick += (s, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0) Choose((CatalogEntry)list.Items[i]); };
                list.Leave += (s, e) => { if (!Box.Focused) Hide(); };
                form.Controls.Add(list);
            }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var c in found) list.Items.Add(c);
            list.SelectedIndex = 0;
            list.EndUpdate();
            // under the box, kept inside the window
            var at = form.PointToClient(Box.PointToScreen(new Point(0, Box.Height + 1)));
            int w = Math.Max(Box.Width, 400), h = list.ItemHeight * Math.Min(10, found.Count) + 4;
            if (at.X + w > form.ClientSize.Width - 8) at.X = Math.Max(0, form.ClientSize.Width - 8 - w);
            if (at.Y + h > form.ClientSize.Height - 4) at.Y = Math.Max(0, at.Y - Box.Height - 2 - h);
            list.Bounds = new Rectangle(at, new Size(w, h));
            list.Visible = true;
            list.BringToFront();
        }

        void DrawEntry(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var c = (CatalogEntry)list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.AccentBtn : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            string right = c.Id + (c.Source != "" ? "  ·  " + c.Source : "");
            var rs = TextRenderer.MeasureText(right, Theme.Base);
            int rw = Math.Min(rs.Width, e.Bounds.Width / 2);
            var rr = new Rectangle(e.Bounds.Right - rw - 6, e.Bounds.Top, rw, e.Bounds.Height);
            var lr = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - rw - 18, e.Bounds.Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, c.Name, Theme.Base, lr, Theme.Text, flags);
            TextRenderer.DrawText(e.Graphics, right, Theme.Base, rr, sel ? Theme.Text : Theme.Muted, flags | TextFormatFlags.Right);
        }
    }
}
