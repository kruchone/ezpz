using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ezpz
{
    // Sandbox page: a form over <server>_SandboxVars.lua, built from the file itself (see SandboxFile).
    // If the file looks unfamiliar the page says so and offers Notepad instead of guessing.
    public partial class MainForm
    {
        Panel pgSandbox, sbProblemPanel;
        FlowLayoutPanel sbList;
        TextBox tbSbSearch;
        CheckBox chkSbNonDefault;
        Label lblSbState, lblSbProblem;
        SandboxFile sandbox;
        DateTime sandboxStamp;
        readonly List<SandboxRow> sbRows = new List<SandboxRow>();
        float dpi = 1f;

        class SandboxRow
        {
            public SandboxSetting S;
            public Panel Panel;
            public Label Name;
            public Button Reset;
            public Control Editor;
            public string SearchText;
        }

        int Sc(int px) { return (int)Math.Round(px * dpi); }

        bool SandboxDirty { get { return sandbox != null && sandbox.Settings.Any(s => s.Changed); } }

        void BuildSandbox()
        {
            using (var g = CreateGraphics()) dpi = g.DpiX / 96f;
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(20, 14, 20, 14) };
            content.Controls.Add(p);
            pgSandbox = p;

            sbList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg };
            p.Controls.Add(sbList);

            sbProblemPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            lblSbProblem = new Label { Dock = DockStyle.Top, Height = 90, ForeColor = Theme.Warn, Font = Theme.Bold };
            var np = Theme.Btn("Edit in Notepad", Theme.AccentBtn);
            np.Location = new Point(0, 100);
            np.Click += (s, e) => EditFile(S.SandboxPath);
            sbProblemPanel.Controls.Add(np);
            sbProblemPanel.Controls.Add(lblSbProblem);
            p.Controls.Add(sbProblemPanel);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0, 10, 0, 0), WrapContents = false };
            var save = Theme.Btn("Save", Theme.AccentBtn);
            save.Click += (s, e) => SaveSandbox();
            var discard = Theme.Btn("Discard changes");
            discard.Click += (s, e) => { if (!SandboxDirty || Confirm("Discard your unsaved sandbox changes?")) RefreshSandbox(true); };
            var notepad = Theme.Btn("Edit in Notepad");
            notepad.Click += (s, e) =>
            {
                if (SandboxDirty && !Confirm("You have unsaved changes here. Open Notepad anyway? (Save first if you want to keep them.)")) return;
                EditFile(S.SandboxPath);
            };
            lblSbState = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 8, 0, 0) };
            bottom.Controls.AddRange(new Control[] { save, discard, notepad, lblSbState });
            p.Controls.Add(bottom);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false };
            top.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
            tbSbSearch = Theme.Box("");
            tbSbSearch.Width = Sc(300);
            tbSbSearch.Margin = new Padding(0, 5, 16, 0);
            tbSbSearch.TextChanged += (s, e) => FilterSandbox();
            chkSbNonDefault = Theme.Check("Only settings changed from the default", false);
            chkSbNonDefault.Margin = new Padding(0, 8, 0, 0);
            chkSbNonDefault.CheckedChanged += (s, e) => FilterSandbox();
            top.Controls.AddRange(new Control[] { tbSbSearch, chkSbNonDefault });
            p.Controls.Add(top);
            p.Controls.Add(new Label
            {
                Text = "World rules: zombies, loot, time, XP and more. Changes take effect when the server restarts, and some (like loot) only affect parts of the map nobody has visited yet. Saving keeps the previous file as a .bak copy.",
                Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Muted
            });
            p.Controls.Add(new Label { Text = "World rules", Font = Theme.H1, Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text });
        }

        void RefreshSandbox(bool force)
        {
            if (pgSandbox == null) return;
            DateTime stamp = File.Exists(S.SandboxPath) ? File.GetLastWriteTimeUtc(S.SandboxPath) : DateTime.MinValue;
            bool changedOnDisk = sandbox != null && stamp != sandboxStamp;
            if (sandbox != null && !force && !changedOnDisk) { UpdateSandboxState(); return; }
            if (changedOnDisk && SandboxDirty && !force &&
                !Confirm("The sandbox file was changed outside ezpz (Notepad, or the server). Reload it and drop your unsaved changes here?")) return;

            Cursor = Cursors.WaitCursor;
            try
            {
                sandbox = SandboxFile.Load(S.SandboxPath, SandboxFile.LoadLabels(S.InstallDir));
                sandboxStamp = stamp;
                BuildSandboxRows();
            }
            finally { Cursor = Cursors.Default; }
        }

        void BuildSandboxRows()
        {
            sbList.SuspendLayout();
            foreach (var r in sbRows) r.Panel.Dispose();
            sbRows.Clear();
            sbList.Controls.Clear();
            bool ok = sandbox.Problem == null;
            sbProblemPanel.Visible = !ok;
            sbList.Visible = ok;
            if (!ok)
            {
                lblSbProblem.Text = sandbox.Problem + "\n\nTo stay safe, ezpz won't edit this file itself. You can still edit it in Notepad.";
                sbList.ResumeLayout();
                UpdateSandboxState();
                return;
            }
            string section = null;
            foreach (var s in sandbox.Settings)
            {
                if (s.Section != section)
                {
                    section = s.Section;
                    var h = new Label
                    {
                        Text = section == "" ? "General" : Words(section), Font = Theme.H2, ForeColor = Theme.Accent,
                        AutoSize = false, Size = new Size(Sc(900), Sc(36)), TextAlign = ContentAlignment.BottomLeft, Tag = section
                    };
                    sbList.Controls.Add(h);
                }
                var row = MakeSandboxRow(s);
                sbRows.Add(row);
                sbList.Controls.Add(row.Panel);
            }
            sbList.ResumeLayout();
            FilterSandbox();
        }

        static string Words(string key)
        {
            return System.Text.RegularExpressions.Regex.Replace(key, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        }

        SandboxRow MakeSandboxRow(SandboxSetting s)
        {
            var r = new SandboxRow { S = s };
            int textW = Sc(560), edX = Sc(590);
            var panel = new Panel { Width = Sc(900), BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, Sc(6)) };
            r.Name = new Label { Text = s.Label, Font = Theme.Bold, AutoSize = false, Location = new Point(0, Sc(4)), Size = new Size(textW, Sc(22)), UseMnemonic = false };
            panel.Controls.Add(r.Name);
            int h = Sc(28);
            if (s.Description != "")
            {
                var d = new Label { Text = s.Description, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(textW, 0), Location = new Point(0, Sc(26)), UseMnemonic = false };
                panel.Controls.Add(d);
                h = Sc(26) + d.GetPreferredSize(new Size(textW, 0)).Height + Sc(4);
            }
            r.Editor = MakeSandboxEditor(s, r);
            r.Editor.Location = new Point(edX, Sc(2));
            panel.Controls.Add(r.Editor);
            r.Reset = new Button
            {
                Text = "Default", FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = false,
                Size = new Size(Sc(70), Sc(26)), Location = new Point(edX + Sc(230), Sc(1)), Cursor = Cursors.Hand
            };
            r.Reset.FlatAppearance.BorderColor = Theme.Card;
            r.Reset.Click += (o, e) => { SetEditorValue(r, s.Default); };
            panel.Controls.Add(r.Reset);
            panel.Height = Math.Max(h, Sc(34));
            r.SearchText = (s.Label + " " + s.Key + " " + s.Description + " " + string.Join(" ", s.Options.Select(o => o.Value))).ToLowerInvariant();
            r.Panel = panel;
            StyleSandboxRow(r);
            return r;
        }

        Control MakeSandboxEditor(SandboxSetting s, SandboxRow r)
        {
            switch (s.Kind)
            {
                case SandboxKind.Enum:
                {
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Sc(220), BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
                    foreach (var o in s.Options) cb.Items.Add(new EnumItem(o.Key, o.Value));
                    int cur;
                    int.TryParse(s.Value, out cur);
                    var match = cb.Items.Cast<EnumItem>().FirstOrDefault(i => i.Value == cur);
                    if (match == null) { match = new EnumItem(cur, "Custom (" + cur + ")"); cb.Items.Add(match); }
                    cb.SelectedItem = match;
                    cb.SelectedIndexChanged += (o, e) => { s.Value = ((EnumItem)cb.SelectedItem).Value.ToString(CultureInfo.InvariantCulture); StyleSandboxRow(r); UpdateSandboxState(); };
                    return cb;
                }
                case SandboxKind.Bool:
                {
                    var ck = new CheckBox { Text = "On", Checked = s.Value == "true", AutoSize = true, ForeColor = Theme.Text };
                    ck.CheckedChanged += (o, e) => { s.Value = ck.Checked ? "true" : "false"; StyleSandboxRow(r); UpdateSandboxState(); };
                    return ck;
                }
                case SandboxKind.Int:
                case SandboxKind.Float:
                {
                    double v;
                    double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                    bool isFloat = s.Kind == SandboxKind.Float;
                    var n = new NumericUpDown
                    {
                        Width = Sc(220), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                        DecimalPlaces = isFloat ? 2 : 0, Increment = isFloat ? 0.1m : 1m, ThousandsSeparator = !isFloat,
                    };
                    decimal min = s.Min.HasValue ? (decimal)s.Min.Value : -1000000000m, max = s.Max.HasValue ? (decimal)s.Max.Value : 1000000000m;
                    decimal cur = (decimal)v;
                    n.Minimum = Math.Min(min, cur);
                    n.Maximum = Math.Max(max, cur);
                    n.Value = cur;
                    n.ValueChanged += (o, e) => { s.Value = SandboxFile.FormatNumber((double)n.Value, s.Kind); if (SameNumber(s.Value, s.Original)) s.Value = s.Original; StyleSandboxRow(r); UpdateSandboxState(); };
                    return n;
                }
                default:
                {
                    string inner = s.Value.Length >= 2 && s.Value.StartsWith("\"") && s.Value.EndsWith("\"") ? s.Value.Substring(1, s.Value.Length - 2) : s.Value;
                    var tb = Theme.Box(inner);
                    tb.Width = Sc(220);
                    bool quoted = s.Value.StartsWith("\"");
                    tb.TextChanged += (o, e) =>
                    {
                        s.Value = quoted ? "\"" + tb.Text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : tb.Text;
                        StyleSandboxRow(r); UpdateSandboxState();
                    };
                    return tb;
                }
            }
        }

        static bool SameNumber(string a, string b)
        {
            double x, y;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y) && Math.Abs(x - y) < 1e-9;
        }

        class EnumItem
        {
            public int Value; public string Text;
            public EnumItem(int v, string t) { Value = v; Text = t; }
            public override string ToString() { return Text; }
        }

        void SetEditorValue(SandboxRow r, string raw)
        {
            if (raw == null) return;
            var cb = r.Editor as ComboBox;
            if (cb != null) { int v; if (int.TryParse(raw, out v)) { var it = cb.Items.Cast<EnumItem>().FirstOrDefault(i => i.Value == v); if (it != null) cb.SelectedItem = it; } return; }
            var ck = r.Editor as CheckBox;
            if (ck != null) { ck.Checked = raw == "true"; return; }
            var n = r.Editor as NumericUpDown;
            if (n != null)
            {
                double d;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)d));
            }
        }

        // Name colour: amber = unsaved change, blue = saved but not the game's default.
        void StyleSandboxRow(SandboxRow r)
        {
            var s = r.S;
            r.Name.ForeColor = s.Changed ? Theme.Warn : (s.Default != null && !s.IsDefault) ? Theme.Info : Theme.Text;
            r.Reset.Visible = s.Default != null && !s.IsDefault;
        }

        void FilterSandbox()
        {
            if (sandbox == null || sandbox.Problem != null) return;
            string q = tbSbSearch.Text.Trim().ToLowerInvariant();
            bool onlyChanged = chkSbNonDefault.Checked;
            sbList.SuspendLayout();
            var visibleSections = new HashSet<string>();
            foreach (var r in sbRows)
            {
                bool show = (q == "" || r.SearchText.Contains(q)) && (!onlyChanged || r.S.Changed || (r.S.Default != null && !r.S.IsDefault));
                r.Panel.Visible = show;
                if (show) visibleSections.Add(r.S.Section);
            }
            foreach (Control c in sbList.Controls)
                if (c is Label && c.Tag is string) c.Visible = visibleSections.Contains((string)c.Tag);
            sbList.ResumeLayout();
            UpdateSandboxState();
        }

        void UpdateSandboxState()
        {
            if (lblSbState == null) return;
            if (sandbox == null || sandbox.Problem != null) { lblSbState.Text = ""; return; }
            int changed = sandbox.Settings.Count(s => s.Changed);
            int nonDefault = sandbox.Settings.Count(s => s.Default != null && !s.IsDefault);
            int shown = sbRows.Count(r => r.Panel.Visible);
            lblSbState.Text = sandbox.Settings.Count + " settings · " + nonDefault + " differ from the default"
                + (shown < sbRows.Count ? " · showing " + shown : "")
                + (changed > 0 ? "  ·  " + changed + " unsaved change" + (changed == 1 ? "" : "s") : "");
            lblSbState.ForeColor = changed > 0 ? Theme.Warn : Theme.Muted;
        }

        void SaveSandbox()
        {
            if (sandbox == null || sandbox.Problem != null || !SandboxDirty) return;
            if (File.Exists(S.SandboxPath) && File.GetLastWriteTimeUtc(S.SandboxPath) != sandboxStamp)
            {
                Warn("The sandbox file was changed outside ezpz since you opened it. Discard changes to reload it, then make your edits again.");
                return;
            }
            try
            {
                var changed = sandbox.Settings.Where(s => s.Changed).Select(s => s.Key + "=" + s.Value).ToList();
                int n = sandbox.Save();
                sandboxStamp = File.GetLastWriteTimeUtc(S.SandboxPath);
                Log("[ezpz] Saved " + n + " sandbox change(s): " + string.Join(", ", changed));
                foreach (var r in sbRows) StyleSandboxRow(r);
                UpdateSandboxState();
                if (server.State == ServerState.Running && Confirm("Saved. Sandbox changes apply when the server restarts. Restart it now?"))
                {
                    var _ = RestartFlow(false);
                }
            }
            catch (Exception ex) { Warn("Couldn't save: " + ex.Message); }
        }
    }
}
