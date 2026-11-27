using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using SimHub.Plugins;

namespace TruckingAdvisor.SimHubPlugin
{
    /// <summary>
    /// Switches SimHub's screens to the TruckingAdvisor triple-screen dashboards while TruckingAdvisor runs, and puts
    /// back what they showed before once TruckingAdvisor closes (or SimHub does).
    ///
    /// Every 2 seconds it asks TruckingAdvisor's local web server (http://localhost:PORT/simhub/dash.json). When the
    /// answer says the SimHub panel is on, it remembers each round and 4" screen's dashboard and shows
    /// "TruckingAdvisor Triple Left / Center / Right" instead. After three missed answers (about 6 seconds) it restores
    /// them. What was on each screen is also kept in PluginsData\TruckingAdvisor, so a SimHub that was killed while
    /// switched restores them on its next start.
    ///
    /// Only SimHub's plugin interface is compiled against; the devices are reached by reflection along SimHub's own
    /// public members (PluginManager.GetPlugin&lt;DevicesPlugin&gt;().DevicesPluginSettings.Devices[*].GetInstances(),
    /// IBitmapDisplayDevice.GetDashboard/SetDashboard), so a SimHub that renames one is logged, not crashed.
    /// </summary>
    [PluginName("TruckingAdvisor")]
    [PluginDescription("Shows the TruckingAdvisor triple-screen dashboards on your round and 4\" screens while TruckingAdvisor runs, and puts your own dashboards back when it closes.")]
    [PluginAuthor("TruckingAdvisor")]
    public sealed class TruckingAdvisorPlugin : IPlugin
    {
        private const int PollMs = 2000;
        private const int MissesBeforeRestore = 3;
        private const string BitmapDisplayInterface = "SimHub.Plugins.OutputPlugins.GraphicalDash.BitmapDisplay.IBitmapDisplayDevice";

        private readonly object _gate = new object();
        private Timer _timer;
        private PluginManager _manager;
        private string _dataDir;
        private int _port = 8789;
        private int _misses;
        private volatile bool _ending;
        /// <summary>Screen key -> the dashboard it showed before we switched it. Empty: nothing switched.</summary>
        private Dictionary<string, string> _previous = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _lastScreensLog;

        public PluginManager PluginManager { get; set; }

        private string StatePath => Path.Combine(_dataDir, "previous-dashboards.txt");
        private string LogPath => Path.Combine(_dataDir, "plugin.log");

        public void Init(PluginManager pluginManager)
        {
            _manager = pluginManager;
            string simHubDir = AppDomain.CurrentDomain.BaseDirectory;
            _dataDir = Path.Combine(simHubDir, "PluginsData", "TruckingAdvisor");
            try { Directory.CreateDirectory(_dataDir); } catch { }
            try
            {
                string config = Path.Combine(Path.GetDirectoryName(typeof(TruckingAdvisorPlugin).Assembly.Location) ?? simHubDir, "TruckingAdvisor.SimHubPlugin.cfg");
                if (File.Exists(config)) _port = TriplePlan.ParsePort(File.ReadAllText(config));
            }
            catch { }
            try { if (File.Exists(StatePath)) _previous = TriplePlan.Deserialize(File.ReadAllText(StatePath)); } catch { }
            Log($"Started (TruckingAdvisor at localhost:{_port}){(_previous.Count > 0 ? $"; {_previous.Count} screen(s) still to restore from last time" : "")}.");
            // The first check waits a little so SimHub has found its screens.
            _timer = new Timer(_ => Tick(), null, 5000, PollMs);
        }

        public void End(PluginManager pluginManager)
        {
            // No lock here: a check in progress may be waiting for the UI thread, which may be the one calling End.
            _ending = true;
            try { _timer?.Dispose(); } catch { }
            // Put the player's dashboards back before SimHub saves its device settings on the way out. If SimHub
            // saved first, the file kept in PluginsData restores them at the next start.
            try { OnUi(Restore, TimeSpan.FromSeconds(3)); } catch (Exception e) { Log("Restoring on exit failed: " + e.Message); }
        }

        private void Tick()
        {
            if (!Monitor.TryEnter(_gate)) return; // the last check is still running
            try
            {
                if (_ending) return;
                var app = TriplePlan.ParseAppState(Fetch());
                if (_ending) return;
                if (app.Ok) _misses = 0; else _misses++;
                if (app.Ok && app.AutoTriple) OnUi(() => Switch(app.SwapRounds));
                else if (_previous.Count > 0 && (_misses >= MissesBeforeRestore || (app.Ok && !app.AutoTriple))) OnUi(Restore);
            }
            catch (Exception e) { Log("Check failed: " + e.Message); }
            finally { Monitor.Exit(_gate); }
        }

        private string Fetch()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"http://localhost:{_port}/simhub/dash.json");
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return reader.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>Shows the triple set on the screens that fit it, remembering what each showed. Repeats are harmless.</summary>
        private void Switch(bool swapRounds)
        {
            // A check that passed its _ending test may still have queued this on the UI thread; once End has
            // started (and restored the player's dashboards), switching again would undo that.
            if (_ending) return;
            var screens = Screens();
            var plan = TriplePlan.Assign(screens.Select(s => s.Info), swapRounds);
            LogScreens(screens, plan);
            bool changed = false;
            foreach (var s in screens)
            {
                if (!plan.TryGetValue(s.Info.Key, out string target)) continue;
                string current = s.Info.Dashboard ?? "";
                if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) continue;
                if (!DashboardInstalled(target))
                {
                    LogOnce("missing:" + target, $"\"{target}\" isn't in SimHub's dashboards yet: add the triple-screen set from TruckingAdvisor.");
                    continue;
                }
                // Remember the player's own dashboard only the first time (not ours after a swap of left and right).
                if (!_previous.ContainsKey(s.Info.Key) && !TriplePlan.IsOurs(current)) _previous[s.Info.Key] = current;
                else if (!_previous.ContainsKey(s.Info.Key)) _previous[s.Info.Key] = "";
                Invoke(s.Device, "SetDashboard", target);
                Log($"{s.Info.Name}: {Show(current)} -> {target}.");
                changed = true;
            }
            if (changed) SaveState();
        }

        /// <summary>Puts back every screen we switched that still shows one of our dashboards.</summary>
        private void Restore()
        {
            if (_previous.Count == 0) return;
            var screens = Screens().ToDictionary(s => s.Info.Key, StringComparer.Ordinal);
            foreach (var kv in _previous.ToList())
            {
                if (!screens.TryGetValue(kv.Key, out var s)) continue; // unplugged: try again next time
                if (TriplePlan.IsOurs(s.Info.Dashboard) && !string.IsNullOrEmpty(kv.Value))
                {
                    Invoke(s.Device, "SetDashboard", kv.Value);
                    Log($"{s.Info.Name}: back to {kv.Value}.");
                }
                _previous.Remove(kv.Key);
            }
            SaveState();
            SaveDeviceSettings();
        }

        // ---- SimHub's devices, by reflection ----

        private sealed class Screen
        {
            public object Device;
            public ScreenInfo Info;
        }

        private List<Screen> Screens()
        {
            var found = new List<Screen>();
            object manager = _manager ?? PluginManager;
            var devicesPlugin = DevicesPlugin(manager);
            var settings = Get(devicesPlugin, "DevicesPluginSettings");
            if (!(Get(settings, "Devices") is IEnumerable roots)) return found;
            foreach (var root in roots.Cast<object>().Where(r => r != null).ToList())
            {
                int index = 0;
                var instances = Invoke(root, "GetInstances") as IEnumerable;
                foreach (var device in (instances ?? new object[0]).Cast<object>())
                {
                    if (device == null || device.GetType().GetInterface(BitmapDisplayInterface) == null) continue;
                    var display = Get(Get(device, "Settings"), "BitmapDisplayInstance");
                    int width = AsInt(Get(display, "DisplayWidth")), height = AsInt(Get(display, "DisplayHeight"));
                    string key = (Get(device, "InstanceId") ?? Get(root, "InstanceId"))?.ToString() + "#" + index++;
                    found.Add(new Screen
                    {
                        Device = device,
                        Info = new ScreenInfo
                        {
                            Key = key,
                            Name = Get(root, "MainDisplayName") as string ?? "Screen",
                            Width = width,
                            Height = height,
                            Dashboard = Invoke(device, "GetDashboard") as string
                        }
                    });
                }
            }
            return found;
        }

        private static object DevicesPlugin(object manager)
        {
            if (manager == null) return null;
            var type = manager.GetType().Assembly.GetType("SimHub.Plugins.Devices.DevicesPlugin", false);
            var getPlugin = manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetPlugin" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            return type == null || getPlugin == null ? null : getPlugin.MakeGenericMethod(type).Invoke(manager, null);
        }

        private void SaveDeviceSettings()
        {
            try { Invoke(DevicesPlugin(_manager ?? PluginManager), "SaveSettings"); }
            catch (Exception e) { Log("Saving SimHub's device settings failed: " + e.Message); }
        }

        private static bool DashboardInstalled(string code)
        {
            try { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DashTemplates", code, code + ".djson")); }
            catch { return true; }
        }

        private static object Get(object target, string property)
        {
            if (target == null) return null;
            var p = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(target);
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            if (target == null) return null;
            var m = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null)
            {
                // Explicit interface implementations.
                m = target.GetType().GetInterfaces().SelectMany(i => i.GetMethods())
                    .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            }
            if (m == null) throw new MissingMethodException(target.GetType().Name, method);
            return m.Invoke(target, args);
        }

        private static int AsInt(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); } catch { return 0; }
        }

        /// <summary>Runs on SimHub's UI thread, where its device settings are changed.</summary>
        private static void OnUi(Action action, TimeSpan? timeout = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, timeout ?? TimeSpan.FromSeconds(10));
        }

        // ---- State and log ----

        private void SaveState()
        {
            try
            {
                if (_previous.Count == 0) { if (File.Exists(StatePath)) File.Delete(StatePath); }
                else File.WriteAllText(StatePath, TriplePlan.Serialize(_previous));
            }
            catch (Exception e) { Log("Couldn't save the previous dashboards: " + e.Message); }
        }

        private void LogScreens(List<Screen> screens, Dictionary<string, string> plan)
        {
            string text = string.Join("; ", screens.Select(s =>
                $"{s.Info.Name} {s.Info.Width}x{s.Info.Height} showing {Show(s.Info.Dashboard)}" +
                (plan.TryGetValue(s.Info.Key, out var t) ? $" -> {t}" : " (left alone)")));
            if (text == _lastScreensLog) return;
            _lastScreensLog = text;
            Log(screens.Count == 0 ? "No SimHub screens found." : "Screens: " + text);
        }

        private readonly HashSet<string> _logged = new HashSet<string>();
        private void LogOnce(string key, string message) { if (_logged.Add(key)) Log(message); }

        private static string Show(string dashboard) => string.IsNullOrEmpty(dashboard) ? "(none)" : dashboard;

        private void Log(string message)
        {
            try
            {
                if (_dataDir == null) return;
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
