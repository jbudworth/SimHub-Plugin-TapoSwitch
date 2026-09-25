using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameReaderCommon;
using Newtonsoft.Json;
using SimHub.Plugins;
using SimHub.Plugin.TapoSwitch.Settings;
using SimHub.Plugin.TapoSwitch.Tapo;

namespace SimHub.Plugin.TapoSwitch
{
    /// <summary>
    /// SimHub plugin that exposes Tapo/TP-Link wifi smart switches as actions
    /// ("Turn on" / "Turn off" / "Toggle") inside Controls and Events, and as
    /// read-only properties reflecting their current state.
    ///
    /// NOTE ON SIMHUB SDK VERSIONS
    /// SimHub's plugin SDK (SimHub.Plugins.dll / GameReaderCommon.dll, referenced
    /// from your local SimHub install) has changed slightly across releases -
    /// mainly around the exact overloads of AddAction/AttachDelegate and the
    /// IWPFSettingsV2 members. This file uses the long-standing, most commonly
    /// documented shapes of those APIs. If your installed SimHub version differs,
    /// the compiler errors will point at the exact line to adjust - see the
    /// README for the most likely fixes.
    /// </summary>
    [PluginDescription("Control Tapo/TP-Link wifi smart switches from Controls and Events")]
    [PluginAuthor("Claude.ai")]
    [PluginName("Tapo Smart Switch")]
    public class TapoPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string SettingsKey = "TapoSwitchPluginSettings";

        public PluginManager PluginManager { get; set; }

        private TapoPluginSettings _settings;
        private readonly Dictionary<string, ISmartSwitchClient> _clients = new Dictionary<string, ISmartSwitchClient>();
        private readonly object _clientsLock = new object();

        private Timer _pollTimer;

        public string LeftMenuTitle => "Tapo Smart Switch";

        public ImageSource PictureIcon => BuildIcon();

        // ------------------------------------------------------------------
        // IPlugin
        // ------------------------------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            _settings = this.ReadCommonSettings<TapoPluginSettings>(SettingsKey, () => new TapoPluginSettings());
            if (_settings.Devices == null)
            {
                _settings.Devices = new System.Collections.ObjectModel.ObservableCollection<TapoDeviceConfig>();
            }

            SimHub.Logging.Current.Info($"SimHub.Plugin.TapoSwitch: initializing with {_settings.Devices.Count} configured switch(es).");

            RegisterActionsForAllDevices();

            var intervalMs = Math.Max(5, _settings.PollingIntervalSeconds) * 1000;
            // An unhandled exception on a timer thread terminates the whole
            // process on .NET Framework, so nothing may escape this callback.
            _pollTimer = new Timer(_ =>
            {
                try { PollAllDevices(); }
                catch (Exception ex) { SimHub.Logging.Current.Warn("SimHub.Plugin.TapoSwitch: poll tick failed.", ex); }
            }, null, 2000, intervalMs);
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("SimHub.Plugin.TapoSwitch: shutting down.");

            _pollTimer?.Dispose();

            lock (_clientsLock)
            {
                foreach (var client in _clients.Values)
                {
                    try { client.Dispose(); } catch { /* in-flight request may still hold the client */ }
                }
                _clients.Clear();
            }
        }

        // ------------------------------------------------------------------
        // IDataPlugin - no telemetry is consumed; switches are controlled
        // independently of any running game/sim, so this is intentionally a
        // no-op. Required by the interface.
        // ------------------------------------------------------------------

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
        }

        // ------------------------------------------------------------------
        // IWPFSettingsV2
        // ------------------------------------------------------------------

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this, _settings);
        }

        // ------------------------------------------------------------------
        // Settings / actions plumbing (called from the settings UI)
        // ------------------------------------------------------------------

        public void SaveSettingsAndRefreshActions()
        {
            this.SaveCommonSettings(SettingsKey, _settings);

            // Drop cached clients so edited credentials/IPs take effect on the
            // next action or poll, but defer the actual Dispose past the longest
            // client timeout so an in-flight poll/action doesn't get an
            // ObjectDisposedException mid-request.
            List<ISmartSwitchClient> oldClients;
            lock (_clientsLock)
            {
                oldClients = _clients.Values.ToList();
                _clients.Clear();
            }
            _ = Task.Delay(TimeSpan.FromSeconds(12)).ContinueWith(_ =>
            {
                foreach (var client in oldClients)
                {
                    try { client.Dispose(); } catch { }
                }
            });

            var intervalMs = Math.Max(5, _settings.PollingIntervalSeconds) * 1000;
            _pollTimer?.Change(intervalMs, intervalMs);

            RegisterActionsForAllDevices();
            PollAllDevices();
        }

        public async Task<bool> TestConnectionAsync(TapoDeviceConfig device)
        {
            using (var client = CreateClient(device))
            {
                return await client.GetIsOnAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes the current switch list to a portable JSON file. Passwords
        /// are deliberately excluded - see ExportedSettings for why.
        /// </summary>
        public void ExportSettings(string filePath)
        {
            var export = new ExportedSettings
            {
                PollingIntervalSeconds = _settings.PollingIntervalSeconds,
                Devices = _settings.Devices.Select(d => new ExportedDeviceConfig
                {
                    Name = d.Name,
                    IpAddress = d.IpAddress,
                    Protocol = d.Protocol.ToString(),
                    Email = d.Email
                }).ToList()
            };

            string json = JsonConvert.SerializeObject(export, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Replaces the current switch list (in the in-memory settings only -
        /// caller still needs to Save) with the contents of a previously
        /// exported JSON file. Returns how many switches were loaded.
        /// Passwords always come back blank; Klap-protocol switches need
        /// their password re-entered after importing.
        /// </summary>
        public int ImportSettings(string filePath)
        {
            string json = File.ReadAllText(filePath);
            var imported = JsonConvert.DeserializeObject<ExportedSettings>(json);
            if (imported?.Devices == null)
            {
                throw new InvalidOperationException("That file doesn't look like a Tapo Smart Switch export.");
            }

            _settings.Devices.Clear();
            foreach (var d in imported.Devices)
            {
                if (!Enum.TryParse(d.Protocol, true, out SwitchProtocol protocol))
                {
                    protocol = SwitchProtocol.Legacy;
                }

                _settings.Devices.Add(new TapoDeviceConfig
                {
                    Name = d.Name,
                    IpAddress = d.IpAddress,
                    Protocol = protocol,
                    Email = d.Email
                });
            }

            if (imported.PollingIntervalSeconds > 0)
            {
                _settings.PollingIntervalSeconds = imported.PollingIntervalSeconds;
            }

            return imported.Devices.Count;
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private static ISmartSwitchClient CreateClient(TapoDeviceConfig device)
        {
            return device.Protocol == SwitchProtocol.Legacy
                ? (ISmartSwitchClient)new LegacyKasaClient(device.IpAddress)
                : new TapoClient(device.IpAddress, device.Email, device.Password);
        }

        private ISmartSwitchClient GetOrCreateClient(TapoDeviceConfig device)
        {
            lock (_clientsLock)
            {
                if (_clients.TryGetValue(device.Id, out var existing))
                {
                    return existing;
                }

                var client = CreateClient(device);
                _clients[device.Id] = client;
                return client;
            }
        }

        private void RegisterActionsForAllDevices()
        {
            // Two names that sanitize identically ("My Desk"/"My_Desk", or two
            // blank names) must not share action names, or one device's actions
            // would silently control the other.
            var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in _settings.Devices.ToList())
            {
                string group = SanitizeForActionName(device.Name);
                string unique = group;
                int n = 2;
                while (!usedGroups.Add(unique)) unique = $"{group}_{n++}";
                RegisterActionsForDevice(device, unique);
            }
        }

        private void RegisterActionsForDevice(TapoDeviceConfig device, string group)
        {
            // Closures capture only the device Id and resolve the current config
            // at fire time: registrations can't be removed once added, so a
            // renamed/removed device would otherwise leave live actions bound to
            // a stale TapoDeviceConfig instance.
            string deviceId = device.Id;

            try
            {
                this.AddAction($"TapoSwitch.{group}.TurnOn", (pm, actionName) =>
                {
                    FireAndForget(async () =>
                    {
                        var dev = FindDevice(deviceId);
                        if (dev == null) return;
                        var client = GetOrCreateClient(dev);
                        await client.SetOnAsync(true).ConfigureAwait(false);
                        dev.LastStatus = "ON";
                    });
                });

                this.AddAction($"TapoSwitch.{group}.TurnOff", (pm, actionName) =>
                {
                    FireAndForget(async () =>
                    {
                        var dev = FindDevice(deviceId);
                        if (dev == null) return;
                        var client = GetOrCreateClient(dev);
                        await client.SetOnAsync(false).ConfigureAwait(false);
                        dev.LastStatus = "OFF";
                    });
                });

                this.AddAction($"TapoSwitch.{group}.Toggle", (pm, actionName) =>
                {
                    FireAndForget(async () =>
                    {
                        var dev = FindDevice(deviceId);
                        if (dev == null) return;
                        var client = GetOrCreateClient(dev);
                        bool nowOn = await client.ToggleAsync().ConfigureAwait(false);
                        dev.LastStatus = nowOn ? "ON" : "OFF";
                    });
                });

                // Exposes e.g. TapoSwitch.MyDesk.State as a usable SimHub property
                // (dashboards, formulas, other plugins). "Connected - ON" is what
                // the settings screen's Test button reports.
                this.AttachDelegate($"TapoSwitch.{group}.State", () =>
                {
                    var dev = FindDevice(deviceId);
                    return dev != null && (dev.LastStatus == "ON" || dev.LastStatus == "Connected - ON");
                });
            }
            catch (Exception ex)
            {
                // Re-registering an existing action/property name on save can
                // throw on some SimHub versions; the original registration keeps
                // working via the Id lookup above, so don't fail the whole save.
                SimHub.Logging.Current.Debug($"SimHub.Plugin.TapoSwitch: re-registration for '{device.Name}' skipped: {ex.Message}");
            }
        }

        private TapoDeviceConfig FindDevice(string id)
        {
            try
            {
                return _settings.Devices.FirstOrDefault(d => d.Id == id);
            }
            catch (InvalidOperationException)
            {
                // Collection mutated by the UI thread mid-enumeration.
                return null;
            }
        }

        private void PollAllDevices()
        {
            List<TapoDeviceConfig> devices;
            try
            {
                // Snapshot: the UI thread can mutate this ObservableCollection
                // while we run on a timer thread. Skip the tick on a clash.
                devices = _settings.Devices.ToList();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            foreach (var device in devices)
            {
                if (string.IsNullOrWhiteSpace(device.IpAddress)) continue;

                FireAndForget(async () =>
                {
                    try
                    {
                        var client = GetOrCreateClient(device);
                        bool isOn = await client.GetIsOnAsync().ConfigureAwait(false);
                        device.LastStatus = isOn ? "ON" : "OFF";
                    }
                    catch (Exception ex)
                    {
                        device.LastStatus = "Unreachable";
                        SimHub.Logging.Current.Debug($"SimHub.Plugin.TapoSwitch: poll failed for '{device.Name}' ({device.IpAddress}): {ex.Message}");
                    }
                });
            }
        }

        private static void FireAndForget(Func<Task> work)
        {
            _ = Task.Run(async () =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Logs to SimHub's own log file/debug window via the log4net-based
                    // ILog exposed by SimHub.Logging.dll.
                    SimHub.Logging.Current.Error("SimHub.Plugin.TapoSwitch action failed.", ex);
                }
            });
        }

        private static string SanitizeForActionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Switch";
            return Regex.Replace(name, "[^A-Za-z0-9_]", "_");
        }

        private static ImageSource BuildIcon()
        {
            // SimHub renders sidebar icons as template glyphs: it recolors every
            // pixel with any opacity to solid white and uses the alpha channel
            // purely as a mask (that's why every other icon in the sidebar is a
            // flat white silhouette on transparent, no color/shading, and no
            // background chip). So the fill color drawn here is irrelevant -
            // only the shape's coverage matters.
            //
            // Shape: a wall outlet/socket face - a circular plate with two
            // vertical slots and a round ground hole punched out, matching the
            // reference mains-socket icon. The punched holes are made
            // transparent (rather than drawn in a second color) using an
            // EvenOdd-filled geometry group, since only alpha survives anyway.
            try
            {
                var plate = new EllipseGeometry(new System.Windows.Point(32, 32), 26, 26);
                var leftSlot = new RectangleGeometry(new System.Windows.Rect(21, 14, 6, 17), 2, 2);
                var rightSlot = new RectangleGeometry(new System.Windows.Rect(37, 14, 6, 17), 2, 2);
                var groundHole = new EllipseGeometry(new System.Windows.Point(32, 41), 5, 5);

                var outlet = new GeometryGroup { FillRule = FillRule.EvenOdd };
                outlet.Children.Add(plate);
                outlet.Children.Add(leftSlot);
                outlet.Children.Add(rightSlot);
                outlet.Children.Add(groundHole);

                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawGeometry(Brushes.White, null, outlet);
                }
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
