using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SPT.Launcher.Helpers
{
    public static class ValidationUtil
    {
        static string c1 = @"Software\Wow6432Node\Valve\Steam";
        static string c3 = "3932890";

        public static string a()
        {
            var c = b();
            if (string.IsNullOrEmpty(c))
                return null;

            var f = d(Path.Combine(c, "steamapps", "libraryfolders.vdf"), "path");
            return f.Length > 0 ? g(f) : null;
        }

        private static string b()
        {
            var h = string.Empty;
            using (var i = Registry.LocalMachine.OpenSubKey(c1, false))
                if (i != null) h = i.GetValue("InstallPath")?.ToString();

            return h;
        }

        private static string g(string[] j)
        {
            var k = $"appmanifest_{c3}.acf";
            foreach (var l in j)
            {
                var m = Path.Combine(l, "steamapps", k);
                if (!File.Exists(m)) continue;

                var n = d(m, "installdir");
                if (n.Length > 0) return Path.Combine(l, "steamapps", "common", n[0]);
            }

            return null;
        }

        private static string[] d(string l, string k)
        {
            if (!File.Exists(l)) return Array.Empty<string>();
            var q = new List<string>();
            var s = $@"""{k}""\s+""(.*)""";
            foreach (var r in File.ReadLines(l))
            {
                var p = Regex.Match(r, s);
                if (p.Success) q.Add(Regex.Unescape(p.Groups[1].Value));
            }

            return q.ToArray();
        }
    }
}
