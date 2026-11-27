#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TruckingAdvisor.SimHubPlugin
{
    /// <summary>One screen SimHub drives (a VoCore or another USB display), as the plugin sees it.</summary>
    public sealed class ScreenInfo
    {
        /// <summary>Stable id: the device's SimHub instance id (plus its position inside a composite device).</summary>
        public string Key;
        /// <summary>The name SimHub shows for the device (used to tell the two round screens apart).</summary>
        public string Name;
        public int Width;
        public int Height;
        /// <summary>The dashboard it shows now (SimHub's dashboard code: the dashboard's folder name).</summary>
        public string Dashboard;
    }

    /// <summary>
    /// Which screen gets which TruckingAdvisor dashboard, and what to put back afterwards. No SimHub types, so it is
    /// tested on its own.
    /// </summary>
    public static class TriplePlan
    {
        public const string Left = "TruckingAdvisor Triple Left";
        public const string Center = "TruckingAdvisor Triple Center";
        public const string Right = "TruckingAdvisor Triple Right";
        public static readonly string[] Dashboards = { Left, Center, Right };

        /// <summary>A round screen reports a square resolution (800 x 800 on a 3.4" VoCore round).</summary>
        public static bool IsRound(int width, int height) =>
            width > 0 && height > 0 && Math.Abs(width - height) <= Math.Max(width, height) * 0.03;

        /// <summary>The small middle screen: a 5:3-ish panel such as the 4" VoCore's 800 x 480 (either way up).</summary>
        public static bool IsCenter(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            double ratio = (double)Math.Max(width, height) / Math.Min(width, height);
            return ratio >= 1.45 && ratio <= 1.9 && Math.Max(width, height) <= 1024;
        }

        /// <summary>
        /// Screen key -> TruckingAdvisor dashboard. The first two round screens (by name, then key) are left and
        /// right, unless <paramref name="swapRounds"/>; the first 4"-class screen is the centre. Other screens keep
        /// what they show.
        /// </summary>
        public static Dictionary<string, string> Assign(IEnumerable<ScreenInfo> screens, bool swapRounds)
        {
            var list = (screens ?? Enumerable.Empty<ScreenInfo>()).Where(s => s != null && !string.IsNullOrEmpty(s.Key)).ToList();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var rounds = list.Where(s => IsRound(s.Width, s.Height))
                .OrderBy(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key, StringComparer.Ordinal).Take(2).ToList();
            if (rounds.Count > 0) result[rounds[0].Key] = swapRounds && rounds.Count > 1 ? Right : Left;
            if (rounds.Count > 1) result[rounds[1].Key] = swapRounds ? Left : Right;
            var center = list.Where(s => IsCenter(s.Width, s.Height))
                .OrderBy(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key, StringComparer.Ordinal).FirstOrDefault();
            if (center != null) result[center.Key] = Center;
            return result;
        }

        public static bool IsOurs(string dashboard) =>
            dashboard != null && Dashboards.Any(d => string.Equals(d, dashboard, StringComparison.OrdinalIgnoreCase));

        // ---- What was on each screen before (kept in a file too, so a SimHub that crashed can still put it back) ----

        /// <summary>One "key TAB dashboard" line per screen. A screen that showed nothing is saved as an empty dashboard.</summary>
        public static string Serialize(IDictionary<string, string> previous)
        {
            var sb = new StringBuilder();
            foreach (var kv in previous.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(Escape(kv.Key)).Append('\t').Append(Escape(kv.Value ?? "")).Append('\n');
            return sb.ToString();
        }

        public static Dictionary<string, string> Deserialize(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (text ?? "").Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                result[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1).TrimEnd('\r'));
            }
            return result;
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ---- TruckingAdvisor's answer ----

        /// <summary>What the plugin needs from TruckingAdvisor's /simhub/dash.json.</summary>
        public sealed class AppState
        {
            /// <summary>TruckingAdvisor answered and its SimHub panel is on.</summary>
            public bool Ok;
            /// <summary>The player wants the triple set shown automatically (TruckingAdvisor setting).</summary>
            public bool AutoTriple = true;
            public bool SwapRounds;
        }

        /// <summary>
        /// Reads the three flags from dash.json without a JSON library (SimHub's Newtonsoft version varies, and the
        /// answer is TruckingAdvisor's own flat object). Anything unreadable is "not ok".
        /// </summary>
        public static AppState ParseAppState(string json)
        {
            var state = new AppState();
            if (string.IsNullOrEmpty(json) || json.TrimStart()[0] != '{') return state;
            state.Ok = ReadBool(json, "ok") == true;
            state.AutoTriple = ReadBool(json, "autoTriple") ?? true;
            state.SwapRounds = ReadBool(json, "swapRounds") ?? false;
            return state;
        }

        private static bool? ReadBool(string json, string name)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + key.Length);
            if (i < 0) return null;
            string rest = json.Substring(i + 1).TrimStart();
            if (rest.StartsWith("true", StringComparison.Ordinal)) return true;
            if (rest.StartsWith("false", StringComparison.Ordinal)) return false;
            return null;
        }

        /// <summary>The port from the config file TruckingAdvisor writes next to the plugin ("port=8789").</summary>
        public static int ParsePort(string config, int fallback = 8789)
        {
            foreach (var line in (config ?? "").Split('\n'))
            {
                var parts = line.Split('=');
                if (parts.Length == 2 && parts[0].Trim().Equals("port", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                    && port >= 1 && port <= 65535)
                    return port;
            }
            return fallback;
        }
    }
}
