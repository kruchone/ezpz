using System;
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
}
