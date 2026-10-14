using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ezpz
{
    static class Program
    {
        public static readonly string Version =
            ((AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(Program).Assembly, typeof(AssemblyInformationalVersionAttribute))).InformationalVersion;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Unexpected error:\n\n" + e.Exception.Message, "ezpz", MessageBoxButtons.OK, MessageBoxIcon.Error);
            string serverName = "";
            try { serverName = AppSettings.Load().ServerName; } catch { }
            var splash = Splash.Start(serverName);

            MainForm f;
            try
            {
                splash.Step("Building the window…", 2);
                f = new MainForm();
                if (args.Length == 2 && args[0] == "--page") f.StartPage = int.Parse(args[1]);
                var handle = f.Handle;   // create the window (not shown yet) so background work can report back to it
            }
            catch (Exception ex)
            {
                splash.Fatal(ex);
                return;
            }

            var problems = f.Preload(splash.Step);
            if (problems.Count > 0 && !splash.ShowProblems(problems)) return;

            f.Shown += (s, e) => { splash.Close(); f.Activate(); };
            Application.Run(f);
        }
    }
}

