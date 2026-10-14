using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // World & Weather page: EBS radio frequency, weather controls, world events.
    public partial class MainForm
    {
        Label lblEbs, lblEbsNote;
        TrackBar tbRain;
        Label lblRainVal;
        NumericUpDown nStorm;
        ComboBox cbWxPlayer;
        string ebsKhz;
        DateTime ebsWhen;

        void BuildWorld()
        {
            var p = pgWorld = NewPage();
            p.H1("World & Weather");

            p.H2("Emergency Broadcast System (EBS)");
            lblEbs = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 26f), ForeColor = Theme.Accent, Text = "—" };
            p.Add(lblEbs, 50);
            lblEbsNote = p.P("");
            p.P("Tune any radio or walkie-talkie to this frequency to hear the automated emergency broadcasts, including the daily weather forecast. The game picks it at random for each new world, so it changes after a reset.");

            p.H2("Weather");
            int y = p.Y;
            tbRain = new TrackBar { Minimum = 1, Maximum = 100, Value = 50, TickFrequency = 10, Width = 300, BackColor = Theme.Bg };
            p.Row("Rain intensity", tbRain, 300);
            lblRainVal = new Label { AutoSize = true, Text = "50", ForeColor = Theme.Text, Location = new Point(Page.X + Page.LabelW + 310, y + 6) };
            p.Controls.Add(lblRainVal);
            tbRain.ValueChanged += (s, e) => lblRainVal.Text = tbRain.Value.ToString();
            p.Buttons(
                WxBtn("Start rain", Theme.AccentBtn, () => "startrain " + tbRain.Value),
                WxBtn("Stop rain", Theme.Card, () => "stoprain"));

            nStorm = p.Row("Storm length (game hours)", Theme.Num(1, 72, 6), 80);
            p.Buttons(
                WxBtn("Start storm", Theme.AccentBtn, () => "startstorm " + nStorm.Value),
                WxBtn("Clear all weather", Theme.Card, () => "stopweather"));

            cbWxPlayer = p.Row("Near player", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat }, 220);
            p.Hint("Online players only.");
            var light = Theme.Btn("Lightning strike");
            light.Click += (s, e) => NearPlayer("lightning");
            var thunder = Theme.Btn("Thunder");
            thunder.Click += (s, e) => NearPlayer("thunder");
            p.Buttons(light, thunder);

            p.H2("World events");
            p.P("Helicopter: a helicopter circles a random player and pulls zombies along behind it (the classic day-one event). Gunshot: a shot rings out near a random player and draws nearby zombies.");
            var chopper = Theme.Btn("Helicopter event");
            chopper.Click += (s, e) => { if (Confirm("Send a helicopter over a random player? It drags a lot of zombies with it.")) Cmd("chopper"); };
            var gun = Theme.Btn("Gunshot");
            gun.Click += (s, e) => Cmd("gunshot");
            p.Buttons(chopper, gun);
            p.End();
        }

        Button WxBtn(string text, Color back, Func<string> cmd)
        {
            var b = Theme.Btn(text, back);
            b.Click += (s, e) =>
            {
                if (server.State != ServerState.Running) { Warn("Start the server first."); return; }
                Cmd(cmd());
            };
            return b;
        }

        void NearPlayer(string cmd)
        {
            if (cbWxPlayer.Text == "") { Warn("No online player selected. (Refresh on the Players page if someone just joined.)"); return; }
            Cmd(cmd + " \"" + Q(cbWxPlayer.Text) + "\"");
        }

        void RefreshWorld()
        {
            if (server.State == ServerState.Running && (DateTime.Now - lastPoll).TotalSeconds > 10) RefreshPlayers(false);
            Task.Run(() =>
            {
                DateTime when;
                string khz = GameData.FindEbs(S, out when);
                Ui(() =>
                {
                    if (khz != null && (ebsKhz == null || when > ebsWhen)) { ebsKhz = khz; ebsWhen = when; }
                    UpdateEbsLabel();
                });
            });
        }

        void UpdateEbsLabel()
        {
            if (lblEbs == null) return;
            if (ebsKhz == null)
            {
                lblEbs.Text = "—";
                lblEbsNote.Text = "Not known yet. It appears in the server log the first time the world starts.";
                return;
            }
            lblEbs.Text = GameData.FormatFreq(ebsKhz);
            lblEbsNote.Text = "From the server log, " + FriendlyTime(ebsWhen) + ".";
        }
    }
}
