using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Ezpz
{
    // Startup splash. It runs on its own UI thread so it keeps painting while the main thread builds and
    // preloads the main window. All methods are safe to call from the main thread.
    public class Splash
    {
        SplashForm form;
        readonly ManualResetEvent ready = new ManualResetEvent(false);

        public static Splash Start()
        {
            var s = new Splash();
            var t = new Thread(() =>
            {
                s.form = new SplashForm();
                s.form.Shown += (o, e) => s.ready.Set();
                Application.Run(s.form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            s.ready.WaitOne(5000);
            return s;
        }

        void Do(Action a)
        {
            try { if (form != null && !form.IsDisposed) form.Invoke(a); } catch { }
        }

        public void Step(string text, int percent) { Do(() => form.SetStep(text, percent)); }

        public void Close() { Do(() => form.Close()); }

        // Shows what went wrong and waits for the user. Returns true for "Continue anyway".
        public bool ShowProblems(List<string> problems)
        {
            bool cont = false;
            var done = new ManualResetEvent(false);
            Do(() => form.ShowProblems("ezpz started, but some things didn't load", problems, true, c => { cont = c; done.Set(); }));
            done.WaitOne();
            return cont;
        }

        public void Fatal(Exception ex)
        {
            var done = new ManualResetEvent(false);
            var lines = new List<string> { ex.Message };
            Do(() => form.ShowProblems("ezpz couldn't start", lines, false, c => done.Set(), ex.ToString()));
            done.WaitOne();
        }
    }

    class SplashForm : Form
    {
        readonly Label lblStep, lblTitle, lblProblemsTitle;
        readonly TextBox tbProblems;
        readonly Panel bar, barFill, problemsPanel;
        readonly Button btnContinue, btnCopy, btnQuit;
        Image logo;
        Action<bool> onChoice;
        string details = "";

        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Text = "ezpz";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 300);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try
            {
                var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("ezpz.png");
                if (st != null) logo = Image.FromStream(st);
            }
            catch { }
            DoubleBuffered = true;

            lblTitle = new Label { Text = "ezpz", Font = new Font("Segoe UI Semibold", 26f), AutoSize = true, Location = new Point(150, 34), ForeColor = Theme.Text };
            var sub = new Label { Text = "Project Zomboid B42 server manager", AutoSize = true, Location = new Point(154, 84), ForeColor = Theme.Muted };
            var ver = new Label { Text = "Version " + Program.Version, AutoSize = true, Location = new Point(154, 106), ForeColor = Theme.Muted };
            lblStep = new Label { Text = "Starting…", AutoSize = false, Location = new Point(30, 196), Size = new Size(500, 22), ForeColor = Theme.Text };
            bar = new Panel { Location = new Point(30, 224), Size = new Size(500, 6), BackColor = Theme.Card };
            barFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 6), BackColor = Theme.Accent };
            bar.Controls.Add(barFill);

            problemsPanel = new Panel { Location = new Point(30, 146), Size = new Size(500, 230), Visible = false };
            lblProblemsTitle = new Label { Location = new Point(0, 0), Size = new Size(500, 22), Font = Theme.Bold, UseMnemonic = false };
            tbProblems = new TextBox
            {
                Location = new Point(0, 26), Size = new Size(500, 150), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, TabStop = false
            };
            btnContinue = Theme.Btn("Continue anyway", Theme.AccentBtn);
            btnContinue.Location = new Point(0, 186);
            btnContinue.Click += (s, e) => Choose(true);
            btnCopy = Theme.Btn("Copy details");
            btnCopy.Location = new Point(150, 186);
            btnCopy.Click += (s, e) => { try { Clipboard.SetText(details); btnCopy.Text = "Copied"; } catch { } };
            btnQuit = Theme.Btn("Quit");
            btnQuit.Location = new Point(270, 186);
            btnQuit.Click += (s, e) => Choose(false);
            problemsPanel.Controls.AddRange(new Control[] { lblProblemsTitle, tbProblems, btnContinue, btnCopy, btnQuit });

            Controls.AddRange(new Control[] { lblTitle, sub, ver, lblStep, bar, problemsPanel });

            // the splash can be dragged around
            MouseDown += Drag;
            foreach (Control c in Controls) c.MouseDown += Drag;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
        void Drag(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || s is Button) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (logo != null) g.DrawImage(logo, 30, 30, 100, 100);
            else using (var b = new SolidBrush(Theme.Accent)) g.DrawString("ez", new Font("Segoe UI", 40f, FontStyle.Bold), b, 30, 34);
            using (var p = new Pen(Theme.Card, 1)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        float DeviceDpiScale() { using (var g = CreateGraphics()) return g.DpiY / 96f; }

        public void SetStep(string text, int percent)
        {
            lblStep.Text = text;
            barFill.Width = (int)(bar.Width * Math.Max(0, Math.Min(100, percent)) / 100.0);
            Refresh();
        }

        public void ShowProblems(string title, List<string> problems, bool canContinue, Action<bool> choice, string detail = null)
        {
            onChoice = choice;
            details = title + Environment.NewLine + string.Join(Environment.NewLine, problems) + (detail != null ? Environment.NewLine + Environment.NewLine + detail : "");
            lblStep.Visible = false;
            bar.Visible = false;
            lblProblemsTitle.Text = title + ":";
            lblProblemsTitle.ForeColor = canContinue ? Theme.Warn : Theme.Danger;
            tbProblems.Text = "• " + string.Join(Environment.NewLine + Environment.NewLine + "• ", problems)
                + Environment.NewLine + Environment.NewLine + "Details are saved in ezpz's log: " + AppLog.TodayPath;
            btnContinue.Visible = canContinue;
            if (!canContinue) { btnCopy.Left = 0; btnQuit.Left = 120; }
            var keep = Location;
            ClientSize = new Size(ClientSize.Width, (int)Math.Round(400 * DeviceDpiScale()));
            Location = new Point(keep.X, Math.Max(0, keep.Y - 50));
            problemsPanel.Visible = true;
            Invalidate();
            TopMost = true; Activate(); TopMost = false;
        }

        void Choose(bool cont)
        {
            var c = onChoice;
            onChoice = null;
            if (c != null) c(cont);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Choose(false);   // closing the window counts as Quit while it's asking
            base.OnFormClosing(e);
        }
    }
}
