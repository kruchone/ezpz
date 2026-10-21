using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Ezpz
{
    public class WorkshopItem
    {
        public string Id;
        public string Title = "";
        public long Size;
        public DateTime Updated;
        public long UpdatedUnix;
        public string[] Tags = new string[0];
        public bool Resolved;
        public bool IsB42Tagged { get { return Tags.Any(t => t.IndexOf("Build 42", StringComparison.OrdinalIgnoreCase) >= 0); } }
    }

    public class ModEntry
    {
        public string Id;
        public string Name = "";
        public string Folder;
        public string WorkshopId;
        public string VersionMin = "";
        public string[] Requires = new string[0];
        public bool HasB42;
        public bool Local;      // from the server's own mods folder, not the Workshop
        public List<string> MapFolders = new List<string>();
    }

    // Steam Workshop lookups (public Web API, no key needed) and SteamCMD downloads.
    public static class Workshop
    {
        public const string GameAppId = "108600";   // mods are published under the game's app id
        const string Api = "https://api.steampowered.com/ISteamRemoteStorage/";

        // Accepts Workshop links, collection links or bare item numbers, in any mix.
        public static List<string> ParseIds(string text)
        {
            var ids = new List<string>();
            foreach (Match m in Regex.Matches(text ?? "", @"(?:\bid=)?\b(\d{6,12})\b"))
                if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            return ids;
        }

        static object Post(string method, NameValueCollection form)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                byte[] resp = wc.UploadValues(Api + method + "/v1/", "POST", form);
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(Encoding.UTF8.GetString(resp));
            }
        }

        static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static object[] Arr(object o) { return o as object[] ?? new object[0]; }
        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }

        public static Dictionary<string, WorkshopItem> GetDetails(IList<string> ids)
        {
            var result = new Dictionary<string, WorkshopItem>();
            for (int start = 0; start < ids.Count; start += 100)
            {
                var batch = ids.Skip(start).Take(100).ToList();
                var form = new NameValueCollection { { "itemcount", batch.Count.ToString() } };
                for (int i = 0; i < batch.Count; i++) form.Add("publishedfileids[" + i + "]", batch[i]);
                var root = Obj(Obj(Post("GetPublishedFileDetails", form))["response"]);
                object list;
                if (!root.TryGetValue("publishedfiledetails", out list)) continue;
                foreach (var o in Arr(list))
                {
                    var d = Obj(o);
                    var it = new WorkshopItem { Id = Str(d, "publishedfileid") };
                    if (Str(d, "result") == "1")
                    {
                        it.Resolved = true;
                        it.Title = Str(d, "title");
                        long n;
                        if (long.TryParse(Str(d, "file_size"), out n)) it.Size = n;
                        if (long.TryParse(Str(d, "time_updated"), out n)) { it.UpdatedUnix = n; it.Updated = DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime; }
                        object tags;
                        if (d.TryGetValue("tags", out tags)) it.Tags = Arr(tags).Select(t => Str(Obj(t), "tag")).ToArray();
                    }
                    result[it.Id] = it;
                }
            }
            return result;
        }

        // For each id that is a collection, its child item ids. Non-collections are left out.
        public static Dictionary<string, List<string>> GetCollections(IList<string> ids)
        {
            var result = new Dictionary<string, List<string>>();
            if (ids.Count == 0) return result;
            var form = new NameValueCollection { { "collectioncount", ids.Count.ToString() } };
            for (int i = 0; i < ids.Count; i++) form.Add("publishedfileids[" + i + "]", ids[i]);
            var root = Obj(Obj(Post("GetCollectionDetails", form))["response"]);
            object list;
            if (!root.TryGetValue("collectiondetails", out list)) return result;
            foreach (var o in Arr(list))
            {
                var d = Obj(o);
                object children;
                if (Str(d, "result") != "1" || !d.TryGetValue("children", out children)) continue;
                var kids = Arr(children).Select(c => Obj(c)).Where(c => Str(c, "filetype") == "0").Select(c => Str(c, "publishedfileid")).ToList();
                if (kids.Count > 0) result[Str(d, "publishedfileid")] = kids;
            }
            return result;
        }

        public static string ContentDir(AppSettings s)
        {
            return Path.Combine(s.InstallDir, "steamapps", "workshop", "content", GameAppId);
        }

        // Workshop id -> "timeupdated" of the copy the server has, from steamapps\workshop\appworkshop_108600.acf.
        public static Dictionary<string, long> InstalledTimes(AppSettings s)
        {
            var d = new Dictionary<string, long>();
            string acf = Path.Combine(s.InstallDir, "steamapps", "workshop", "appworkshop_" + GameAppId + ".acf");
            if (!File.Exists(acf)) return d;
            string text = File.ReadAllText(acf);
            int start = text.IndexOf("\"WorkshopItemsInstalled\"", StringComparison.Ordinal);
            if (start < 0) return d;
            int end = text.IndexOf("\"WorkshopItemDetails\"", start, StringComparison.Ordinal);
            string block = end > start ? text.Substring(start, end - start) : text.Substring(start);
            foreach (Match m in Regex.Matches(block, "\"(\\d+)\"\\s*\\{[^}]*?\"timeupdated\"\\s+\"(\\d+)\"", RegexOptions.Singleline))
                d[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
            return d;
        }

        // Items whose Workshop version is newer than the server's copy (and items not downloaded at all).
        public static List<string> Outdated(AppSettings s, IList<string> ids, out List<string> missing)
        {
            missing = new List<string>();
            var outdated = new List<string>();
            if (ids.Count == 0) return outdated;
            var local = InstalledTimes(s);
            foreach (var kv in GetDetails(ids))
            {
                if (!kv.Value.Resolved) continue;
                long have;
                if (!local.TryGetValue(kv.Key, out have)) missing.Add(kv.Key);
                else if (kv.Value.UpdatedUnix > have) outdated.Add(kv.Key);
            }
            return outdated;
        }

        public static bool IsDownloaded(AppSettings s, string id)
        {
            return Directory.Exists(Path.Combine(ContentDir(s), id, "mods"));
        }

        // Downloads (or updates) items straight into the server's own Workshop folder, where the server looks for them.
        public static async Task<List<string>> Download(AppSettings s, IList<string> ids, Action<string> log)
        {
            await SteamCmd.EnsureInstalled(s, log);
            var args = new StringBuilder("+force_install_dir " + Util.QuoteArg(s.InstallDir) + " +login anonymous");
            foreach (var id in ids) args.Append(" +workshop_download_item " + GameAppId + " " + id);
            args.Append(" +quit");
            log("[steamcmd] Downloading " + ids.Count + " Workshop item(s)...");
            await SteamCmd.RunAsync(s.SteamCmdExe, args.ToString(), s.SteamCmdDir, log);
            return ids.Where(id => !IsDownloaded(s, id)).ToList();
        }
    }

    // Reads mod.info files from downloaded Workshop items.
    public static class ModScanner
    {
        public static List<ModEntry> Scan(AppSettings s, string workshopId)
        {
            return ScanDir(Path.Combine(Workshop.ContentDir(s), workshopId, "mods"), workshopId);
        }

        // Mods copied straight into the server's mods folder (<data>\mods). The game loads them, but players need
        // their own copy: servers only send Workshop mods (through Steam), never mod files.
        public static List<ModEntry> ScanLocal(AppSettings s)
        {
            var list = ScanDir(Path.Combine(s.DataDir, "mods"), "");
            list.RemoveAll(m => m.Id.Equals("examplemod", StringComparison.OrdinalIgnoreCase));   // the game's own sample
            foreach (var m in list) m.Local = true;
            return list;
        }

        static List<ModEntry> ScanDir(string modsDir, string workshopId)
        {
            var list = new List<ModEntry>();
            if (!Directory.Exists(modsDir)) return list;
            foreach (var folder in Directory.GetDirectories(modsDir).OrderBy(d => d))
            {
                // B42 layout: mods/<Folder>/42[.x]/mod.info (+ common/). B41 layout: mods/<Folder>/mod.info.
                string verDir = Directory.GetDirectories(folder)
                    .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^42(\.\d+)*$") && File.Exists(Path.Combine(d, "mod.info")))
                    .OrderByDescending(d => VersionKey(Path.GetFileName(d)))
                    .FirstOrDefault();
                string info = verDir != null ? Path.Combine(verDir, "mod.info") : Path.Combine(folder, "mod.info");
                if (!File.Exists(info)) continue;
                var kv = ReadInfo(info);
                string id;
                if (!kv.TryGetValue("id", out id) || id.Trim() == "") continue;
                var m = new ModEntry
                {
                    Id = id.Trim(),
                    Folder = folder,
                    WorkshopId = workshopId,
                    HasB42 = verDir != null,
                };
                string v;
                if (kv.TryGetValue("name", out v)) m.Name = v.Trim();
                if (kv.TryGetValue("versionMin", out v)) m.VersionMin = v.Trim();
                if (kv.TryGetValue("require", out v))
                    m.Requires = v.Split(',', ';').Select(r => ModConfig.NormalizeId(r)).Where(r => r != "").ToArray();
                foreach (var root in new[] { verDir ?? folder, Path.Combine(folder, "common") })
                {
                    string maps = Path.Combine(root, "media", "maps");
                    if (Directory.Exists(maps))
                        foreach (var md in Directory.GetDirectories(maps))
                            if (!m.MapFolders.Contains(Path.GetFileName(md))) m.MapFolders.Add(Path.GetFileName(md));
                }
                list.Add(m);
            }
            return list;
        }

        static string VersionKey(string v)
        {
            return string.Join(".", v.Split('.').Select(p => p.PadLeft(5, '0')));
        }

        static Dictionary<string, string> ReadInfo(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && !d.ContainsKey(line.Substring(0, eq).Trim())) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }
            return d;
        }
    }

    // The Mods=, WorkshopItems= and Map= lines, and what the last server run said about mods.
    public static class ModConfig
    {
        public const string VanillaMap = "Muldraugh, KY";

        // Mods= entries may be written "Id", "\Id" or "WorkshopId\Id"; the mod id is the last part.
        public static string NormalizeId(string entry)
        {
            string e = (entry ?? "").Trim();
            int i = e.LastIndexOf('\\');
            return (i >= 0 ? e.Substring(i + 1) : e).Trim();
        }

        public static List<string> Split(string value)
        {
            return (value ?? "").Split(';').Select(x => x.Trim()).Where(x => x != "").ToList();
        }

        // B42 format, confirmed on a 42.20 server: a leading backslash on each id.
        public static string FormatMods(IEnumerable<string> ids)
        {
            return string.Join(";", ids.Select(i => "\\" + i));
        }

        public static string BuildMap(string current, IEnumerable<string> enabledMapFolders, IEnumerable<string> allModMapFolders)
        {
            var modMaps = new HashSet<string>(allModMapFolders, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var f in enabledMapFolders) if (!result.Contains(f)) result.Add(f);
            foreach (var f in Split(current))
                if (!modMaps.Contains(f) && !f.Equals(VanillaMap, StringComparison.OrdinalIgnoreCase) && !result.Contains(f)) result.Add(f);
            result.Add(VanillaMap);
            return string.Join(";", result);
        }

        public class RunStatus
        {
            public HashSet<string> Loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Problems = new List<string>();
            public bool Found;
        }

        static readonly Regex LoadingLine = new Regex(@">\s*loading (\S+)\s*$");

        // Reads server-console.txt, which holds the most recent server run.
        public static RunStatus ReadLastRun(AppSettings s)
        {
            var st = new RunStatus();
            if (!File.Exists(s.ConsoleLogPath)) return st;
            string text;
            try { text = GameData.ReadShared(s.ConsoleLogPath); } catch { return st; }
            st.Found = true;
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                var m = LoadingLine.Match(l);
                if (m.Success && l.Contains("Mod")) st.Loaded.Add(m.Groups[1].Value);
                if (l.IndexOf("required mod", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (l.Contains("Workshop:") && l.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    int gt = l.IndexOf('>');
                    string msg = (gt >= 0 ? l.Substring(gt + 1) : l).Trim();
                    if (!st.Problems.Contains(msg)) st.Problems.Add(msg);
                }
            }
            return st;
        }
    }
}
