using System.Collections.ObjectModel;

namespace SimHub.Plugin.TapoSwitch.Settings
{
    /// <summary>Root settings object persisted by SimHub for this plugin.</summary>
    public class TapoPluginSettings
    {
        public ObservableCollection<TapoDeviceConfig> Devices { get; set; }
            = new ObservableCollection<TapoDeviceConfig>();

        // How often (seconds) to poll each device for its current on/off state,
        // used to keep the exposed SimHub property and the settings screen in sync.
        public int PollingIntervalSeconds { get; set; } = 15;
    }
}
