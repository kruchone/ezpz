using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ezpz
{
    public enum ServerState { Stopped, Starting, Running, Stopping }

    // Owns the java.exe server process: start, stop, console I/O, detection of an already-running server.
    public class ServerController
    {
        public event Action<string> Output;
        public event Action StateChanged;
        public event Action UnexpectedExit;

        readonly AppSettings S;
        Process proc;
        StreamWriter stdin;
        int detachedPid;            // a server we found running but didn't launch (no stdin)
        long tailPos = -1;
        bool userStopping;

        public ServerState State { get; private set; }
        public DateTime StartedAt { get; private set; }
        // Local port of PZ's built-in Prometheus metrics server (0 = none). Picked fresh at each launch.
        public int MetricsPort { get; private set; }
        public bool StopWhenStarted;

        public ServerController(AppSettings s) { S = s; State = ServerState.Stopped; }

        public bool Attached { get { return proc != null && !proc.HasExited; } }
        public bool Detached { get { return detachedPid != 0; } }

        public int Pid
        {
            get
            {
                if (Attached) return proc.Id;
                return detachedPid;
            }
        }

        void Emit(string s) { var h = Output; if (h != null) h(s); }
        void SetState(ServerState st)
        {
            if (State == st) return;
            State = st;
            var h = StateChanged; if (h != null) h();
        }

        // Turn StartServer64.bat into a direct java.exe command line so we own the real process.
        int pendingMetricsPort;

        static int FreeLocalPort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        string AdminPasswordArg { get { return " -adminpassword " + Util.QuoteArg(S.AdminPassword); } }

        public void BuildCommand(out string exe, out string args)
        {
            if (!File.Exists(S.StartBat)) throw new Exception("StartServer64.bat not found in " + S.InstallDir + ". Install the server first (Setup page).");
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string javaLine = null;
            foreach (var raw in File.ReadAllLines(S.StartBat))
            {
                string t = raw.Trim().TrimStart('@');
                if (t.Length == 0 || t.StartsWith("::") || t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
                {
                    string body = t.Substring(4).Trim().Trim('"');
                    int eq = body.IndexOf('=');
                    if (eq > 0) vars[body.Substring(0, eq).Trim()] = Expand(body.Substring(eq + 1), vars);
                    continue;
                }
                if (t.IndexOf("zombie.network.GameServer", StringComparison.Ordinal) >= 0) javaLine = t;
            }
            if (javaLine == null) throw new Exception("Couldn't find the java launch line in StartServer64.bat.");

            javaLine = Regex.Replace(javaLine, @"\s%[1-9*]", "");
            javaLine = Expand(javaLine, vars).Trim();
            if (javaLine.StartsWith("\""))
            {
                int q = javaLine.IndexOf('"', 1);
                exe = javaLine.Substring(1, q - 1);
                args = javaLine.Substring(q + 1).Trim();
            }
            else
            {
                int sp = javaLine.IndexOf(' ');
                exe = javaLine.Substring(0, sp);
                args = javaLine.Substring(sp + 1).Trim();
            }
            if (!Path.IsPathRooted(exe)) exe = Path.GetFullPath(Path.Combine(S.InstallDir, exe));

            args = Regex.Replace(args, @"-Xms\d+[kKmMgG]?", "-Xms" + S.MinRamGB + "g");
            args = Regex.Replace(args, @"-Xmx\d+[kKmMgG]?", "-Xmx" + S.MaxRamGB + "g");
            // Java memory log for the Health page. JVM options must come before the main class.
            if (args.IndexOf("-Xlog:gc", StringComparison.Ordinal) < 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(S.GcLogPath));
                string gcArg = Util.QuoteArg("-Xlog:gc,gc+init:file=\"" + S.GcLogPath + "\":time,uptime,level,tags:filecount=5,filesize=20M");
                int main = args.IndexOf("zombie.network.GameServer", StringComparison.Ordinal);
                args = args.Substring(0, main) + gcArg + " " + args.Substring(main);
            }
            // PZ's built-in metrics server (read by the Home page instead of polling the stats console command).
            if (args.IndexOf("-DprometheusPort", StringComparison.Ordinal) < 0)
            {
                pendingMetricsPort = FreeLocalPort();
                int main = args.IndexOf("zombie.network.GameServer", StringComparison.Ordinal);
                args = args.Substring(0, main) + "-DprometheusPort=" + pendingMetricsPort + " " + args.Substring(main);
            }
            args += " -servername " + Util.QuoteArg(S.ServerName);
            args += " " + Util.QuoteArg("-cachedir=" + S.DataDir);
            if (!string.IsNullOrEmpty(S.AdminPassword))
                args += AdminPasswordArg;
        }

        string Expand(string s, Dictionary<string, string> vars)
        {
            string dir = S.InstallDir.TrimEnd('\\') + "\\";
            s = Regex.Replace(s, "%~dp0", dir.Replace("$", "$$"), RegexOptions.IgnoreCase);
            return Regex.Replace(s, "%([A-Za-z_][A-Za-z0-9_]*)%", m =>
            {
                string v;
                if (vars.TryGetValue(m.Groups[1].Value, out v)) return v;
                v = Environment.GetEnvironmentVariable(m.Groups[1].Value);
                return v ?? "";
            });
        }

        public void Start()
        {
            if (State != ServerState.Stopped) throw new Exception("Server is already " + State.ToString().ToLower() + ".");
            string exe, args;
            BuildCommand(out exe, out args);
            Directory.CreateDirectory(S.DataDir);

            string shown = args;
            if (!string.IsNullOrEmpty(S.AdminPassword) && shown.EndsWith(AdminPasswordArg))
                shown = shown.Substring(0, shown.Length - AdminPasswordArg.Length) + " -adminpassword ********";
            Emit("[ezpz] Launching: " + exe + " " + shown);

            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = S.InstallDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            userStopping = false;
            detachedPid = 0;
            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (o, e) => { if (e.Data != null) OnLine(e.Data); };
            p.ErrorDataReceived += (o, e) => { if (e.Data != null) OnLine(e.Data); };
            p.Exited += (o, e) => OnExited(p);
            // Avoid .NET writing a UTF-8 byte-order mark into the server's stdin (happens when the
            // console code page is UTF-8). Only matters when ezpz is run from a console.
            try { if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new UTF8Encoding(false); } catch { }
            p.Start();
            proc = p;
            // .NET may have already written a UTF-8 byte-order mark into stdin; push it onto its own
            // empty line so it can't corrupt the first real command, then write BOM-free UTF-8 ourselves.
            if (p.StandardInput.Encoding.GetPreamble().Length > 0) { p.StandardInput.BaseStream.WriteByte((byte)'\n'); p.StandardInput.BaseStream.Flush(); }
            stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
            stdin.AutoFlush = true;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            StartedAt = DateTime.Now;
            MetricsPort = pendingMetricsPort;
            SetState(ServerState.Starting);
        }

        void OnLine(string line)
        {
            Emit(line);
            if (State == ServerState.Starting && line.IndexOf("SERVER STARTED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetState(ServerState.Running);
                Emit("[ezpz] Server is up and accepting players.");
                if (StopWhenStarted)
                {
                    StopWhenStarted = false;
                    Emit("[ezpz] Default config generated. Stopping server as requested...");
                    Task.Run(() => StopAsync());
                }
            }
        }

        void OnExited(Process p)
        {
            if (p != proc) return;
            int code = -1;
            try { code = p.ExitCode; } catch { }
            bool unexpected = !userStopping && (State == ServerState.Running || State == ServerState.Starting);
            Emit("[ezpz] Server process exited (code " + code + ").");
            proc = null;
            SetState(ServerState.Stopped);
            if (unexpected) { var h = UnexpectedExit; if (h != null) h(); }
        }

        // Sends a console command. Uses stdin when we launched the server, RCON otherwise.
        public void SendCommand(string cmd) { SendCommand(cmd, true); }

        public void SendCommand(string cmd, bool echo)
        {
            if (Attached)
            {
                if (echo) Emit("> " + cmd);
                stdin.Write(cmd + "\n");
                return;
            }
            if (Detached)
            {
                var ini = IniFile.Load(S.IniPath);
                int port; string pw = ini.Get("RCONPassword", "");
                if (!int.TryParse(ini.Get("RCONPort", ""), out port) || pw == "")
                    throw new Exception("This server was started outside the manager. Set RCONPort and RCONPassword on the Configure page (then restart once) so the manager can send commands.");
                if (echo) Emit("> " + cmd + "   (via RCON)");
                string resp = Rcon.Send(port, pw, cmd);
                if (!string.IsNullOrEmpty(resp)) Emit(resp.TrimEnd());
                return;
            }
            throw new Exception("Server is not running.");
        }

        public async Task StopAsync()
        {
            if (State == ServerState.Stopped) return;
            userStopping = true;
            SetState(ServerState.Stopping);
            Emit("[ezpz] Saving world and shutting down (quit)...");
            try { SendCommand("quit"); } catch (Exception ex) { Emit("[ezpz] " + ex.Message); }

            for (int i = 0; i < 180; i++)   // up to 90s for a clean save
            {
                await Task.Delay(500);
                if (!IsAlive()) break;
            }
            if (IsAlive())
            {
                Emit("[ezpz] Server didn't exit in 90s; force-killing.");
                Kill();
            }
            if (!Attached) { detachedPid = 0; SetState(ServerState.Stopped); }
        }

        bool IsAlive()
        {
            if (Attached) return true;
            if (detachedPid != 0)
            {
                try { return !Process.GetProcessById(detachedPid).HasExited; } catch { return false; }
            }
            return false;
        }

        public void Kill()
        {
            userStopping = true;
            int pid = Pid;
            if (pid == 0) return;
            try { Process.GetProcessById(pid).Kill(); } catch { }
            if (!Attached) { detachedPid = 0; SetState(ServerState.Stopped); }
        }

        // Look for a PZ server for this servername that's already running (e.g. manager was closed).
        public void DetectExisting()
        {
            if (Attached) return;
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe'"))
                {
                    foreach (ManagementObject mo in q.Get())
                    {
                        string cl = Convert.ToString(mo["CommandLine"]);
                        if (cl.IndexOf("zombie.network.GameServer", StringComparison.Ordinal) < 0) continue;
                        if (!Regex.IsMatch(cl, @"-servername\s+""?" + Regex.Escape(S.ServerName) + @"(""|\s|$)")) continue;
                        if (detachedPid == 0)
                        {
                            detachedPid = Convert.ToInt32(mo["ProcessId"]);
                            var mp = Regex.Match(cl, @"-DprometheusPort=(\d+)");
                            MetricsPort = mp.Success ? int.Parse(mp.Groups[1].Value) : 0;
                            try { StartedAt = Process.GetProcessById(detachedPid).StartTime; } catch { StartedAt = DateTime.Now; }
                            Emit("[ezpz] Found a running server '" + S.ServerName + "' (PID " + detachedPid + "). Showing its console log; commands go via RCON.");
                            tailPos = File.Exists(S.ConsoleLogPath) ? Math.Max(0, new FileInfo(S.ConsoleLogPath).Length - 4000) : 0;
                            SetState(ServerState.Running);
                        }
                        return;
                    }
                }
            }
            catch { }
            if (detachedPid != 0)
            {
                bool unexpected = !userStopping;
                detachedPid = 0;
                Emit("[ezpz] External server process ended.");
                SetState(ServerState.Stopped);
                if (unexpected) { var h = UnexpectedExit; if (h != null) h(); }
            }
        }

        // For detached servers: follow server-console.txt
        public void TailConsoleLog()
        {
            if (!Detached || tailPos < 0) return;
            try
            {
                if (!File.Exists(S.ConsoleLogPath)) return;
                using (var fs = new FileStream(S.ConsoleLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < tailPos) tailPos = 0;
                    if (fs.Length == tailPos) return;
                    fs.Seek(tailPos, SeekOrigin.Begin);
                    var sr = new StreamReader(fs, Encoding.UTF8);
                    string text = sr.ReadToEnd();
                    tailPos = fs.Length;
                    foreach (var l in text.Split('\n')) if (l.Trim().Length > 0) Emit(l.TrimEnd('\r'));
                }
            }
            catch { }
        }

        public long MemoryBytes()
        {
            try { int pid = Pid; if (pid == 0) return 0; var p = Process.GetProcessById(pid); p.Refresh(); return p.WorkingSet64; }
            catch { return 0; }
        }
    }

    // SteamCMD download / app_update
    public static class SteamCmd
    {
        public const string AppId = "380870";

        public static async Task EnsureInstalled(AppSettings s, Action<string> log)
        {
            if (File.Exists(s.SteamCmdExe)) return;
            Directory.CreateDirectory(s.SteamCmdDir);
            string zip = Path.Combine(s.SteamCmdDir, "steamcmd.zip");
            log("[steamcmd] Downloading SteamCMD from Valve...");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
                await wc.DownloadFileTaskAsync(new Uri("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"), zip);
            ZipFile.ExtractToDirectory(zip, s.SteamCmdDir);
            File.Delete(zip);
            log("[steamcmd] SteamCMD installed. Letting it update itself (first run only)...");
            await RunAsync(s.SteamCmdExe, "+quit", s.SteamCmdDir, log);
            log("[steamcmd] SteamCMD is up to date.");
        }

        public static async Task<bool> InstallOrUpdate(AppSettings s, bool validate, Action<string> log)
        {
            await EnsureInstalled(s, log);
            Directory.CreateDirectory(s.InstallDir);
            string beta = "";
            if (!string.IsNullOrEmpty(s.Branch) && !s.Branch.Equals("public", StringComparison.OrdinalIgnoreCase))
                beta = " -beta " + s.Branch;
            string args = "+force_install_dir " + Util.QuoteArg(s.InstallDir) + " +login anonymous +app_update " + AppId + beta + (validate ? " validate" : "") + " +quit";

            const int attempts = 3;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                log("[steamcmd] steamcmd " + args);
                var cts = new CancellationTokenSource();
                var progress = ReportProgress(s, log, cts.Token);
                int code = await RunAsync(s.SteamCmdExe, args, s.SteamCmdDir, log);
                cts.Cancel();
                await progress;
                log("[steamcmd] Exit code " + code);
                if (File.Exists(s.StartBat) && (code == 0 || attempt == attempts)) { log("[steamcmd] Server files are ready: " + s.InstallDir); return true; }
                if (attempt < attempts) log("[steamcmd] Retrying (SteamCMD often needs another pass after updating itself)...");
            }
            log("[steamcmd] Install did not complete. See messages above.");
            return false;
        }

        // SteamCMD prints almost nothing while downloading when its output is piped, so relay
        // its content log and the size on disk to show it's still working.
        static async Task ReportProgress(AppSettings s, Action<string> log, CancellationToken ct)
        {
            string contentLog = Path.Combine(s.SteamCmdDir, "logs", "content_log.txt");
            long logPos = File.Exists(contentLog) ? new FileInfo(contentLog).Length : 0;
            long lastSize = -1;
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(3000, ct); } catch (TaskCanceledException) { break; }
                try
                {
                    if (File.Exists(contentLog))
                    {
                        using (var fs = new FileStream(contentLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (fs.Length < logPos) logPos = 0;
                            fs.Seek(logPos, SeekOrigin.Begin);
                            string text = new StreamReader(fs).ReadToEnd();
                            logPos = fs.Length;
                            foreach (var l in text.Split('\n'))
                                if (l.Contains("update changed") || l.Contains("Downloading ") || l.Contains("fully installed") || l.Contains("Error"))
                                    log("[steamcmd] " + Regex.Replace(l.Trim(), @"^\[[^\]]*\]\s*", ""));
                        }
                    }
                }
                catch { }
                if (++tick % 5 == 0)   // every ~15s
                {
                    long size = Util.DirSize(s.InstallDir);
                    if (size != lastSize) { log("[steamcmd] Working… " + Util.Size(size) + " in install folder"); lastSize = size; }
                }
            }
        }

        // Only one SteamCMD at a time: an update check, a server update and a mod download must not overlap.
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public static async Task<int> RunAsync(string exe, string args, string wd, Action<string> log)
        {
            await gate.WaitAsync();
            try { return await RunOnce(exe, args, wd, log); }
            finally { gate.Release(); }
        }

        static Task<int> RunOnce(string exe, string args, string wd, Action<string> log)
        {
            var tcs = new TaskCompletionSource<int>();
            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = wd;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("[steamcmd] " + e.Data.Trim()); };
            p.ErrorDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("[steamcmd] " + e.Data.Trim()); };
            p.Exited += (o, e) => { p.WaitForExit(); tcs.TrySetResult(p.ExitCode); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return tcs.Task;
        }

        public static string InstalledBuildId(AppSettings s)
        {
            if (!File.Exists(s.AppManifest)) return null;
            var b = Regex.Match(File.ReadAllText(s.AppManifest), "\"buildid\"\\s+\"(\\d+)\"");
            return b.Success ? b.Groups[1].Value : null;
        }

        // Asks Steam for the newest build of the server on the configured branch. Null if it couldn't tell.
        public static async Task<string> LatestBuildId(AppSettings s, Action<string> log)
        {
            await EnsureInstalled(s, log);
            var sb = new StringBuilder();
            await RunAsync(s.SteamCmdExe, "+login anonymous +app_info_update 1 +app_info_print " + AppId + " +quit", s.SteamCmdDir,
                l => { lock (sb) sb.AppendLine(l.StartsWith("[steamcmd] ") ? l.Substring(11) : l); });
            string text;
            lock (sb) text = sb.ToString();
            int at = text.IndexOf("\"branches\"", StringComparison.Ordinal);
            if (at < 0) return null;
            string branch = string.IsNullOrEmpty(s.Branch) ? "public" : s.Branch;
            var m = Regex.Match(text.Substring(at), "\"" + Regex.Escape(branch) + "\"\\s*\\{[^}]*?\"buildid\"\\s+\"(\\d+)\"", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string InstalledBuildInfo(AppSettings s)
        {
            if (!File.Exists(s.AppManifest)) return null;
            string t = File.ReadAllText(s.AppManifest);
            var b = Regex.Match(t, "\"buildid\"\\s+\"(\\d+)\"");
            var k = Regex.Match(t, "\"BetaKey\"\\s+\"([^\"]*)\"");
            string branch = k.Success && k.Groups[1].Value.Length > 0 ? k.Groups[1].Value : "public";
            return "Steam build " + (b.Success ? b.Groups[1].Value : "?") + " on branch '" + branch + "'";
        }
    }

    // Zip backups of a single server's world, accounts DB and config.
    public static class Backups
    {
        const string ManifestName = "ezpz-backup.txt";
        const string LegacyManifestName = "pzmanager-backup.txt";   // backups made before the rename

        public static string Create(AppSettings s, string reason, Action<string> log)
        {
            Directory.CreateDirectory(s.BackupDir);
            string name = s.ServerName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + reason + ".zip";
            string zip = Path.Combine(s.BackupDir, name);
            int files = 0;
            using (var fs = new FileStream(zip, FileMode.CreateNew))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var m = za.CreateEntry(ManifestName);
                using (var w = new StreamWriter(m.Open()))
                {
                    w.WriteLine("servername=" + s.ServerName);
                    w.WriteLine("created=" + DateTime.Now.ToString("s"));
                    w.WriteLine("reason=" + reason);
                }
                if (Directory.Exists(s.SaveDir))
                    foreach (var f in Directory.GetFiles(s.SaveDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(s.SaveDir.Length).TrimStart('\\').Replace('\\', '/');
                        if (AddFile(za, f, "Saves/Multiplayer/" + s.ServerName + "/" + rel)) files++;
                    }
                if (AddFile(za, s.DbPath, "db/" + s.ServerName + ".db")) files++;
                foreach (var f in s.ConfigFiles)
                    if (AddFile(za, f, "Server/" + Path.GetFileName(f))) files++;
            }
            log("[backup] Created " + name + " (" + files + " files, " + Util.Size(new FileInfo(zip).Length) + ")");
            return zip;
        }

        static bool AddFile(ZipArchive za, string path, string entry)
        {
            if (!File.Exists(path)) return false;
            var e = za.CreateEntry(entry, CompressionLevel.Fastest);
            using (var es = e.Open())
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                src.CopyTo(es);
            return true;
        }

        public static string ServerNameIn(string zip)
        {
            using (var za = ZipFile.OpenRead(zip)) return ServerNameIn(za);
        }

        // ezpz backups carry ezpz-backup.txt; PZ's own carry readme.txt with "ServerName: ...".
        static string ServerNameIn(ZipArchive za)
        {
            var m = za.GetEntry(ManifestName) ?? za.GetEntry(LegacyManifestName);
            if (m != null)
                using (var r = new StreamReader(m.Open()))
                {
                    string l;
                    while ((l = r.ReadLine()) != null)
                        if (l.StartsWith("servername=")) return l.Substring(11);
                }
            var pz = za.GetEntry(PzReadme);
            if (pz != null)
                using (var r = new StreamReader(pz.Open()))
                {
                    string l;
                    while ((l = r.ReadLine()) != null)
                        if (l.StartsWith("ServerName:")) return l.Substring(11).Trim();
                }
            return null;
        }

        const string PzReadme = "readme.txt";

        public class BackupInfo
        {
            public string Path, Server, Kind, WorldVersion = "";
            public bool ByPZ, HasWorld, HasAccounts, HasConfig;
            public DateTime Created;
            public long Size;
            public string Contents
            {
                get
                {
                    var parts = new List<string>();
                    if (HasWorld) parts.Add("world");
                    if (HasAccounts) parts.Add("accounts");
                    if (HasConfig) parts.Add("config");
                    return parts.Count == 0 ? "(empty)" : string.Join(", ", parts);
                }
            }
        }

        static BackupInfo Describe(string zip, bool byPz)
        {
            var fi = new FileInfo(zip);
            var b = new BackupInfo { Path = zip, ByPZ = byPz, Created = fi.LastWriteTime, Size = fi.Length };
            using (var za = ZipFile.OpenRead(zip))
            {
                b.Server = ServerNameIn(za);
                foreach (var e in za.Entries)
                {
                    if (e.FullName.StartsWith("Saves/")) b.HasWorld = true;
                    else if (e.FullName.StartsWith("db/")) b.HasAccounts = true;
                    else if (e.FullName.StartsWith("Server/")) b.HasConfig = true;
                }
                var pz = za.GetEntry(PzReadme);
                if (pz != null)
                    using (var r = new StreamReader(pz.Open()))
                    {
                        string all = r.ReadToEnd();
                        var t = Regex.Match(all, @"Backup time:\s*(\d{4}-\d\d-\d\d) at (\d\d:\d\d:\d\d)");
                        DateTime when;
                        if (t.Success && DateTime.TryParse(t.Groups[1].Value + " " + t.Groups[2].Value, out when)) b.Created = when;
                        var wv = Regex.Match(all, @"World version in this backup is:\s*(\d+)");
                        if (wv.Success) b.WorldVersion = wv.Groups[1].Value;
                    }
            }
            if (byPz) b.Kind = new DirectoryInfo(System.IO.Path.GetDirectoryName(zip)).Name;   // startup, period or version
            else
            {
                var m = Regex.Match(fi.Name, @"_([a-z\-]+)\.zip$");
                b.Kind = m.Success ? (m.Groups[1].Value == "auto" ? "start / scheduled" : m.Groups[1].Value) : "";
            }
            return b;
        }

        // Everything restorable for this server: ezpz's zips in the backups folder and PZ's own in <data>\backups.
        public static List<BackupInfo> ListAll(AppSettings s)
        {
            var list = new List<BackupInfo>();
            if (Directory.Exists(s.BackupDir))
                foreach (var f in Directory.GetFiles(s.BackupDir, s.ServerName + "_*.zip"))
                    try { list.Add(Describe(f, false)); } catch { }
            string pzDir = System.IO.Path.Combine(s.DataDir, "backups");
            if (Directory.Exists(pzDir))
                foreach (var f in Directory.GetFiles(pzDir, "backup_*.zip", SearchOption.AllDirectories))
                {
                    try
                    {
                        var b = Describe(f, true);
                        if (b.Server == null || b.Server.Equals(s.ServerName, StringComparison.OrdinalIgnoreCase)) list.Add(b);
                    }
                    catch { }
                }
            return list.OrderByDescending(b => b.Created).ToList();
        }

        // Caller must ensure the server is stopped and has taken a safety backup.
        // Only replaces what the backup contains: a config-only backup leaves the world alone.
        public static void Restore(AppSettings s, string zip, Action<string> log)
        {
            string name = ServerNameIn(zip);
            if (name == null) throw new Exception("This doesn't look like an ezpz or Project Zomboid server backup.");
            if (!name.Equals(s.ServerName, StringComparison.OrdinalIgnoreCase)) throw new Exception("This backup belongs to server '" + name + "'. Switch to that server first.");
            var info = Describe(zip, false);

            var clear = new List<string>();
            if (info.HasWorld) clear.Add(s.SaveDir);
            if (info.HasAccounts) clear.Add(s.DbPath);
            if (info.HasConfig) clear.AddRange(s.ConfigFiles);
            foreach (var p in clear)
                if (Util.IsInside(p, s.DataDir)) Util.DeletePath(p);

            string root = System.IO.Path.GetFullPath(s.DataDir).TrimEnd('\\') + "\\";
            int n = 0;
            using (var za = ZipFile.OpenRead(zip))
            {
                foreach (var e in za.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    // only the server's own data; not ezpz/PZ notes or PZ's copy of options.ini
                    if (!(e.FullName.StartsWith("Saves/") || e.FullName.StartsWith("db/") || e.FullName.StartsWith("Server/"))) continue;
                    string dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(s.DataDir, e.FullName.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                    using (var es = e.Open())
                    using (var fs = File.Create(dest))
                        es.CopyTo(fs);
                    n++;
                }
            }
            log("[backup] Restored " + info.Contents + " (" + n + " files) from " + System.IO.Path.GetFileName(zip));
        }
        public static void PruneAuto(AppSettings s, Action<string> log)
        {
            if (!Directory.Exists(s.BackupDir) || s.KeepAutoBackups <= 0) return;
            var autos = new DirectoryInfo(s.BackupDir).GetFiles(s.ServerName + "_*_auto.zip")
                .OrderByDescending(f => f.CreationTimeUtc).Skip(s.KeepAutoBackups).ToList();
            foreach (var f in autos)
            {
                try { f.Delete(); log("[backup] Pruned old auto-backup " + f.Name); } catch { }
            }
        }
    }

    // What a reset will delete. Built from the Reset page options.
    public class ResetPlan
    {
        public bool World, KeepCharacters, Accounts, Config, Logs;

        public List<string> Targets(AppSettings s)
        {
            var t = new List<string>();
            if (World)
            {
                if (KeepCharacters && Directory.Exists(s.SaveDir))
                {
                    foreach (var e in Directory.GetFileSystemEntries(s.SaveDir))
                        if (!Path.GetFileName(e).StartsWith("players.db", StringComparison.OrdinalIgnoreCase)) t.Add(e);
                }
                else t.Add(s.SaveDir);
            }
            if (Accounts) { t.Add(s.DbPath); t.Add(s.DbPath + "-journal"); }
            if (Config) t.AddRange(s.ConfigFiles);
            if (Logs) { t.Add(s.LogsDir); t.Add(s.ConsoleLogPath); }
            return t.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }

        public void Execute(AppSettings s, Action<string> log)
        {
            foreach (var p in Targets(s))
            {
                if (!Util.IsInside(p, s.DataDir)) { log("[reset] SKIPPED (outside data folder): " + p); continue; }
                Util.DeletePath(p);
                log("[reset] Deleted " + p);
            }
        }
    }
}
