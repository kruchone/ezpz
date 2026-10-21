using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ezpz
{
    // ezpz's own log: what ezpz did (starts, stops, backups, commands sent, problems) and any error with its full
    // details, one file per day in %APPDATA%\ezpz\logs. The game server's own output is not copied here; Project
    // Zomboid already keeps that in the server's data folder. Meant to be attached to bug reports.
    public static class AppLog
    {
        static readonly object gate = new object();
        const int KeepDays = 14;

        public static string Dir { get { return Path.Combine(AppSettings.ManagerDir, "logs"); } }
        public static string TodayPath { get { return Path.Combine(Dir, "ezpz-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"); } }

        // Called once at startup: a header for the session, and old files cleaned up.
        public static void StartSession()
        {
            Write("========== ezpz " + Program.Version + " started  (" + WindowsVersion() + ", .NET " + Environment.Version + ")");
            try
            {
                foreach (var f in Directory.GetFiles(Dir, "ezpz-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)))
                    File.Delete(f);
            }
            catch { }
        }

        // e.g. "Windows 11 Home 25H2 build 26200" (Environment.OSVersion stops at Windows 8 without a manifest)
        static string WindowsVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = Convert.ToString(k.GetValue("ProductName")), build = Convert.ToString(k.GetValue("CurrentBuild"));
                    int b;
                    if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");   // the registry still says 10
                    return name + " " + k.GetValue("DisplayVersion") + " build " + build;
                }
            }
            catch { return "Windows " + Environment.OSVersion.Version; }
        }

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(TodayPath, DateTime.Now.ToString("HH:mm:ss") + "  " + Scrub(line) + Environment.NewLine);
                }
            }
            catch { }   // logging must never break the app
        }

        // An error with everything needed to track it down: where it happened and the full stack trace.
        public static void Error(string where, Exception ex)
        {
            Write("ERROR in " + where + ": " + (ex == null ? "(no details)" : ex.ToString().Replace(Environment.NewLine, Environment.NewLine + "          ")));
        }

        // Passwords never go in the file, even when they were part of a command.
        static readonly Regex PwCmd = new Regex("^(> (?:setpassword|adduser)\\s+(?:\"[^\"]*\"|\\S+)\\s+)(\"[^\"]*\"|\\S+)", RegexOptions.IgnoreCase);
        static readonly Regex PwArg = new Regex("(-adminpassword|RCONPassword=|Password=)\\s*\\S+", RegexOptions.IgnoreCase);
        static readonly Regex Webhook = new Regex(@"https://(?:\w+\.)?discord(?:app)?\.com/api/webhooks/\S+", RegexOptions.IgnoreCase);

        static string Scrub(string s)
        {
            s = PwCmd.Replace(s, "$1\"********\"");
            s = PwArg.Replace(s, "$1 ********");
            s = Webhook.Replace(s, "<discord webhook>");
            return s;
        }
    }
}
