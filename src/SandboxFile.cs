using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ezpz
{
    public enum SandboxKind { Enum, Bool, Int, Float, Text }

    public class SandboxSetting
    {
        public string Section = "";      // "" for top level, else e.g. "ZombieLore"
        public string Key;
        public int Line;                 // index into the file's lines
        public string Original;          // raw value as in the file
        public string Value;             // raw value to write (may differ from Original)
        public SandboxKind Kind;
        public string Description = "";
        public List<KeyValuePair<int, string>> Options = new List<KeyValuePair<int, string>>();
        public string Default;           // raw value, when the file says
        public double? Min, Max;
        public string Label = "";

        public bool Changed { get { return Value != Original; } }
        public bool IsDefault { get { return Default != null && Same(Value, Default); } }

        static bool Same(string a, string b)
        {
            double x, y;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return Math.Abs(x - y) < 1e-9;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Reads and edits <server>_SandboxVars.lua without rewriting its structure: only the value on a
    // setting's own line ever changes. The descriptions, choices and Min/Max/Default come from the
    // comments the game writes above each setting, so new settings in future builds just appear.
    public class SandboxFile
    {
        public string Path;
        public string[] Lines;
        public List<SandboxSetting> Settings = new List<SandboxSetting>();
        public string Problem;           // non-null if the file isn't safe to edit here
        string newline = "\r\n";
        bool bom;

        static readonly Regex TableStart = new Regex(@"^\s*(\w+)\s*=\s*\{\s*$");
        static readonly Regex TableEnd = new Regex(@"^\s*\}\s*,?\s*$");
        static readonly Regex Assign = new Regex(@"^(\s*)(\w+)(\s*=\s*)(.*?)(\s*,?\s*)$");
        static readonly Regex OptionLine = new Regex(@"^\s*--\s*(-?\d+)\s*=\s*(.+?)\s*$");
        static readonly Regex MinMaxDef = new Regex(@"Min:\s*(-?[\d.]+)\s+Max:\s*(-?[\d.]+)\s+Default:\s*(\S+)");
        static readonly Regex DefaultName = new Regex(@"Default\s*=\s*(.+?)\s*$");
        static readonly Regex Number = new Regex(@"^-?\d+(\.\d+)?$");

        public static SandboxFile Load(string path, Dictionary<string, string> labels)
        {
            var f = new SandboxFile { Path = path };
            if (!File.Exists(path)) { f.Problem = "There's no SandboxVars file yet. Generate the default config on the Setup page first."; return f; }
            byte[] raw = File.ReadAllBytes(path);
            f.bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(raw, f.bom ? 3 : 0, raw.Length - (f.bom ? 3 : 0));
            f.newline = text.Contains("\r\n") ? "\r\n" : "\n";
            f.Lines = text.Replace("\r\n", "\n").Split('\n');
            f.Parse(labels ?? new Dictionary<string, string>());
            return f;
        }

        void Parse(Dictionary<string, string> labels)
        {
            var stack = new Stack<string>();
            var comments = new List<string>();
            bool seenRoot = false;
            for (int i = 0; i < Lines.Length; i++)
            {
                string l = Lines[i];
                string t = l.Trim();
                if (t == "") { comments.Clear(); continue; }
                if (t.StartsWith("--")) { comments.Add(t); continue; }
                var ts = TableStart.Match(l);
                if (ts.Success)
                {
                    if (!seenRoot && ts.Groups[1].Value != "SandboxVars") { Problem = "This file doesn't start with \"SandboxVars = {\" as expected."; return; }
                    seenRoot = true;
                    stack.Push(ts.Groups[1].Value);
                    comments.Clear();
                    continue;
                }
                if (TableEnd.IsMatch(l))
                {
                    if (stack.Count == 0) { Problem = "The file's braces don't match up."; return; }
                    stack.Pop();
                    comments.Clear();
                    continue;
                }
                var a = Assign.Match(l);
                if (!a.Success || stack.Count == 0) { Problem = "Line " + (i + 1) + " isn't in a form ezpz understands: " + t; return; }
                string key = a.Groups[2].Value, val = a.Groups[4].Value;
                if (val.EndsWith(",")) val = val.TrimEnd(',').TrimEnd();
                var s = new SandboxSetting { Key = key, Line = i, Original = val, Value = val };
                s.Section = stack.Count > 1 ? string.Join(".", stack.Reverse().Skip(1)) : "";
                Describe(s, comments);
                string lab;
                s.Label = labels.TryGetValue("Sandbox_" + key, out lab) ? lab : Words(key);
                if (key != "VERSION") Settings.Add(s);
                comments.Clear();
            }
            if (stack.Count != 0) Problem = "The file ends before all its sections are closed.";
            else if (!seenRoot) Problem = "No SandboxVars table found.";
            else if (Settings.Count < 20) Problem = "Only " + Settings.Count + " settings found, which is fewer than expected.";
        }

        static void Describe(SandboxSetting s, List<string> comments)
        {
            var desc = new List<string>();
            foreach (var c in comments)
            {
                var o = OptionLine.Match(c);
                if (o.Success) { s.Options.Add(new KeyValuePair<int, string>(int.Parse(o.Groups[1].Value), o.Groups[2].Value)); continue; }
                desc.Add(c.TrimStart('-').Trim());
            }
            string d = string.Join(" ", desc);
            var mm = MinMaxDef.Match(d);
            if (mm.Success)
            {
                s.Min = double.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                s.Max = double.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);
                s.Default = mm.Groups[3].Value;
                d = d.Substring(0, mm.Index).Trim();
            }
            string v = s.Value;
            if (s.Options.Count > 0 && Regex.IsMatch(v, @"^-?\d+$"))
            {
                s.Kind = SandboxKind.Enum;
                var dn = DefaultName.Match(d);
                if (dn.Success)
                {
                    var match = s.Options.FirstOrDefault(o => o.Value.Equals(dn.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match.Value != null) s.Default = match.Key.ToString(CultureInfo.InvariantCulture);
                    d = d.Substring(0, dn.Index).Trim().TrimEnd('.');
                }
            }
            else if (v == "true" || v == "false") s.Kind = SandboxKind.Bool;
            else if (Number.IsMatch(v)) s.Kind = v.Contains(".") || (s.Default != null && s.Default.Contains(".")) ? SandboxKind.Float : SandboxKind.Int;
            else s.Kind = SandboxKind.Text;
            s.Description = d;
        }

        static string Words(string key)
        {
            return Regex.Replace(key, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        }

        public static string FormatNumber(double v, SandboxKind kind)
        {
            if (kind == SandboxKind.Int) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            string s = v.ToString("0.0###", CultureInfo.InvariantCulture);
            return s;
        }

        // Writes changed values back onto their own lines. Keeps a .bak of the previous file.
        public int Save()
        {
            int n = 0;
            foreach (var s in Settings.Where(x => x.Changed))
            {
                var a = Assign.Match(Lines[s.Line]);
                if (!a.Success || a.Groups[2].Value != s.Key) throw new InvalidOperationException("The file changed on disk since it was opened (" + s.Key + "). Reload and try again.");
                string tail = a.Groups[4].Value.EndsWith(",") ? "," + a.Groups[5].Value : a.Groups[5].Value;
                Lines[s.Line] = a.Groups[1].Value + s.Key + a.Groups[3].Value + s.Value + tail;
                n++;
            }
            if (n == 0) return 0;
            File.Copy(Path, Path + ".bak", true);
            string text = string.Join(newline, Lines);
            byte[] body = new UTF8Encoding(false).GetBytes(text);
            using (var fs = new FileStream(Path, FileMode.Create, FileAccess.Write))
            {
                if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
            foreach (var s in Settings) s.Original = s.Value;
            return n;
        }

        // Display names from the game's own English translations, if the install has them.
        public static Dictionary<string, string> LoadLabels(string installDir)
        {
            var d = new Dictionary<string, string>();
            try
            {
                string f = System.IO.Path.Combine(installDir, "media", "lua", "shared", "Translate", "EN", "Sandbox.json");
                if (!File.Exists(f)) return d;
                foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"(Sandbox_[A-Za-z0-9_]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    if (!m.Groups[1].Value.EndsWith("_tooltip") && !d.ContainsKey(m.Groups[1].Value))
                        d[m.Groups[1].Value] = Regex.Unescape(m.Groups[2].Value);
            }
            catch { }
            return d;
        }
    }
}
