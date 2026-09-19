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
            _pollTimer = new Timer(_ => PollAllDevices(), null, 2000, intervalMs);
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("SimHub.Plugin.TapoSwitch: shutting down.");

            _pollTimer?.Dispose();

            lock (_clientsLock)
            {
                foreach (var client in _clients.Values) client.Dispose();
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

            // Drop any cached clients so edited credentials/IPs take effect
            // on the next action or poll.
            lock (_clientsLock)
            {
                foreach (var client in _clients.Values) client.Dispose();
                _clients.Clear();
            }

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
            foreach (var device in _settings.Devices.ToList())
            {
                RegisterActionsForDevice(device);
            }
        }

        private void RegisterActionsForDevice(TapoDeviceConfig device)
        {
            string group = SanitizeForActionName(device.Name);

            this.AddAction($"TapoSwitch.{group}.TurnOn", (pm, actionName) =>
            {
                FireAndForget(async () =>
                {
                    var client = GetOrCreateClient(device);
                    await client.SetOnAsync(true).ConfigureAwait(false);
                    device.LastStatus = "ON";
                });
            });

            this.AddAction($"TapoSwitch.{group}.TurnOff", (pm, actionName) =>
            {
                FireAndForget(async () =>
                {
                    var client = GetOrCreateClient(device);
                    await client.SetOnAsync(false).ConfigureAwait(false);
                    device.LastStatus = "OFF";
                });
            });

            this.AddAction($"TapoSwitch.{group}.Toggle", (pm, actionName) =>
            {
                FireAndForget(async () =>
                {
                    var client = GetOrCreateClient(device);
                    bool nowOn = await client.ToggleAsync().ConfigureAwait(false);
                    device.LastStatus = nowOn ? "ON" : "OFF";
                });
            });

            // Exposes e.g. TapoSwitch.MyDesk.State as a usable SimHub property
            // (dashboards, formulas, other plugins).
            this.AttachDelegate($"TapoSwitch.{group}.State", () => device.LastStatus == "ON");
        }

        private void PollAllDevices()
        {
            foreach (var device in _settings.Devices.ToList())
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
