using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Ezpz
{
    public class CatalogEntry
    {
        public string Id;            // Base.Axe
        public string Name;          // Axe (English display name, or the id when there is none)
        public string Category = ""; // DisplayCategory for items
        public string Source = "";   // "" for the game, else the mod's name
        public string Search;        // lower-case name + id, for matching
    }

    // Every item and vehicle the server knows, read from the installed server's script files and the
    // enabled mods' scripts, with English names from their translation files. Always matches the
    // server's exact build and mod list.
    public class ItemCatalog
    {
        public List<CatalogEntry> Items = new List<CatalogEntry>();
        public List<CatalogEntry> Vehicles = new List<CatalogEntry>();
        public string Key = "";   // what it was built from; rebuilt when this changes

        Dictionary<string, CatalogEntry> itemById, vehicleById;

        public CatalogEntry FindItem(string id) { return Find(itemById, id); }
        public CatalogEntry FindVehicle(string id) { return Find(vehicleById, id); }

        static CatalogEntry Find(Dictionary<string, CatalogEntry> d, string id)
        {
            CatalogEntry e;
            if (d == null || string.IsNullOrEmpty(id)) return null;
            if (!id.Contains(".")) id = "Base." + id;
            return d.TryGetValue(id, out e) ? e : null;
        }

        // A key for the install + enabled mods, so callers can tell when the catalog is stale.
        public static string KeyFor(AppSettings s)
        {
            var ini = IniFile.Load(s.IniPath);
            return s.InstallDir + "|" + ini.Get("WorkshopItems", "") + "|" + ini.Get("Mods", "");
        }

        public static ItemCatalog Load(AppSettings s)
        {
            var c = new ItemCatalog { Key = KeyFor(s) };
            var items = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var vehicles = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // the game first, then mods in load order (later definitions win, as in the game)
            var roots = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(Path.Combine(s.InstallDir, "media"), "") };
            roots.AddRange(ModRoots(s));
            foreach (var r in roots)
            {
                ReadNames(Path.Combine(r.Key, "lua", "shared", "Translate", "EN"), names);
                string scripts = Path.Combine(r.Key, "scripts");
                if (!Directory.Exists(scripts)) continue;
                foreach (var f in SafeFiles(scripts))
                    ParseScript(f, r.Value, items, vehicles);
            }

            foreach (var e in items.Values)
            {
                string n;
                if (names.TryGetValue(e.Id, out n) || names.TryGetValue("ItemName_" + e.Id, out n)) e.Name = n;
            }
            foreach (var e in vehicles.Values)
            {
                string n, bare = e.Id.Substring(e.Id.IndexOf('.') + 1);
                if (names.TryGetValue("IGUI_VehicleName" + bare, out n) || (e.Category != "" && names.TryGetValue("IGUI_VehicleName" + e.Category, out n))) e.Name = n;
                if (bare.IndexOf("Burnt", StringComparison.OrdinalIgnoreCase) >= 0 && e.Name.IndexOf("Burnt", StringComparison.OrdinalIgnoreCase) < 0) e.Name += " (burnt)";
                e.Category = "";
            }
            foreach (var e in items.Values.Concat(vehicles.Values)) e.Search = (e.Name + " " + e.Id).ToLowerInvariant();

            c.Items = items.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            c.Vehicles = vehicles.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            c.itemById = items;
            c.vehicleById = vehicles;
            return c;
        }

        // media folders of the enabled mods, with the mod's name. B42 mods have common/ and 42[.x]/ folders.
        static IEnumerable<KeyValuePair<string, string>> ModRoots(AppSettings s)
        {
            var ini = IniFile.Load(s.IniPath);
            var enabled = ModConfig.Split(ini.Get("Mods", "")).Select(ModConfig.NormalizeId).ToList();
            if (enabled.Count == 0) return new KeyValuePair<string, string>[0];
            var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var ws in ModConfig.Split(ini.Get("WorkshopItems", "")))
                foreach (var m in ModScanner.Scan(s, ws))
                    if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;
            var list = new List<KeyValuePair<string, string>>();
            foreach (var id in enabled)
            {
                ModEntry m;
                if (!byId.TryGetValue(id, out m)) continue;
                string label = m.Name != "" ? m.Name : m.Id;
                list.Add(new KeyValuePair<string, string>(Path.Combine(m.Folder, "media"), label));   // B41 layout
                list.Add(new KeyValuePair<string, string>(Path.Combine(m.Folder, "common", "media"), label));
                var ver = Directory.GetDirectories(m.Folder)
                    .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^42(\.\d+)*$"))
                    .OrderBy(d => string.Join(".", Path.GetFileName(d).Split('.').Select(p => p.PadLeft(5, '0'))));
                foreach (var d in ver) list.Add(new KeyValuePair<string, string>(Path.Combine(d, "media"), label));
            }
            return list;
        }

        static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase); }
            catch { return new string[0]; }
        }

        static readonly Regex BlockComment = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);
        static readonly Regex Header = new Regex(@"^(module|item|vehicle)\s+([\w.\-]+)\s*(\{.*)?$");

        // Script files are "module X { item Y { Field = value, ... } vehicle Z { ... } }". Only top-level
        // blocks inside a module count; "item 1 [Base.X]" lines in recipes are deeper and ignored.
        static void ParseScript(string file, string source, Dictionary<string, CatalogEntry> items, Dictionary<string, CatalogEntry> vehicles)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { return; }
            text = BlockComment.Replace(text, "");
            string module = null, pendingKind = null, pendingName = null;
            CatalogEntry cur = null;
            Dictionary<string, CatalogEntry> curInto = null;
            bool curHidden = false;
            int depth = 0, curDepth = -1;
            Action open = () =>
            {
                depth++;
                if (pendingKind == "module") module = pendingName;
                else
                {
                    cur = new CatalogEntry { Id = module + "." + pendingName, Name = pendingName, Source = source };
                    curInto = pendingKind == "vehicle" ? vehicles : items;
                    curDepth = depth;
                    curHidden = false;
                }
                pendingKind = null;
            };
            foreach (var raw in text.Split('\n'))
            {
                string line = raw;
                int cm = line.IndexOf("//");
                if (cm >= 0) line = line.Substring(0, cm);
                line = line.Trim();
                if (line == "") continue;

                var h = depth <= 1 && (line.StartsWith("module") || line.StartsWith("item") || line.StartsWith("vehicle")) ? Header.Match(line) : Match.Empty;
                if (h.Success && (h.Groups[1].Value == "module" ? depth == 0 : depth == 1 && module != null))
                {
                    pendingKind = h.Groups[1].Value;
                    pendingName = h.Groups[2].Value;
                    line = h.Groups[3].Value;   // "{", or a whole one-line block like "{ Weight = 1, }"
                }
                if (cur != null && depth == curDepth)
                {
                    int eq = line.IndexOf('=');
                    string k = eq > 0 ? line.Substring(0, eq).Trim().ToLowerInvariant() : "";
                    if (k == "displayname" || k == "displaycategory" || k == "carmodelname" || k == "obsolete")
                    {
                        string v = line.Substring(eq + 1).Trim().TrimEnd(',').Trim();
                        if (k == "displayname") cur.Name = v;
                        else if (k == "displaycategory" || k == "carmodelname") cur.Category = v;   // vehicles: carModelName helps find the name
                        else if (k == "obsolete" && v.Equals("true", StringComparison.OrdinalIgnoreCase)) curHidden = true;
                    }
                }
                foreach (char ch in line)
                {
                    if (ch == '{')
                    {
                        if (pendingKind != null) open();
                        else depth++;
                    }
                    else if (ch == '}')
                    {
                        depth = Math.Max(0, depth - 1);
                        if (cur != null && depth < curDepth)
                        {
                            if (!curHidden) curInto[cur.Id] = cur;
                            cur = null; curDepth = -1;
                        }
                        if (depth == 0) module = null;
                    }
                }
            }
        }

        // English names: B42 uses JSON files (ItemName.json, IG_UI.json); older mods use ItemName_EN.txt style
        // files with lines like  ItemName_Base.Axe = "Axe",
        static readonly Regex OldStyle = new Regex("^\\s*([\\w.\\-]+)\\s*=\\s*\"(.*)\"\\s*,?\\s*$");

        static void ReadNames(string dir, Dictionary<string, string> names)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new[] { "ItemName.json", "IG_UI.json" })
            {
                string p = Path.Combine(dir, f);
                if (!File.Exists(p)) continue;
                try
                {
                    var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(p));
                    foreach (var kv in d) if (kv.Value is string) names[kv.Key] = (string)kv.Value;
                }
                catch { }
            }
            foreach (var f in new[] { "ItemName_EN.txt", "IG_UI_EN.txt" })
            {
                string p = Path.Combine(dir, f);
                if (!File.Exists(p)) continue;
                try
                {
                    foreach (var line in File.ReadAllLines(p))
                    {
                        var m = OldStyle.Match(line);
                        if (m.Success) names[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                }
                catch { }
            }
        }

        // Best matches for what the user typed: every word must appear in the name or id. Exact ids first,
        // then names starting with the text, then word starts, then anything else.
        public static List<CatalogEntry> Search(List<CatalogEntry> list, string query, int max)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            if (q == "") return new List<CatalogEntry>();
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return list.Where(e => words.All(w => e.Search.Contains(w)))
                .Select(e => new { e, rank = Rank(e, q) })
                .OrderBy(x => x.rank).ThenBy(x => x.e.Name.Length).ThenBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max).Select(x => x.e).ToList();
        }

        static int Rank(CatalogEntry e, string q)
        {
            string id = e.Id.ToLowerInvariant(), name = e.Name.ToLowerInvariant();
            if (id == q || id.EndsWith("." + q)) return 0;
            if (name == q) return 1;
            if (name.StartsWith(q)) return 2;
            if (id.Substring(id.IndexOf('.') + 1).StartsWith(q)) return 3;
            if (Regex.IsMatch(name, @"\b" + Regex.Escape(q))) return 4;
            return 5;
        }
    }
}
