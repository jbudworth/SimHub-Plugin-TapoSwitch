using System.Collections.Generic;

namespace SimHub.Plugin.TapoSwitch.Settings
{
    /// <summary>
    /// Stable file format for Export/Import, kept separate from
    /// TapoPluginSettings/TapoDeviceConfig so internal storage changes (like
    /// the DPAPI-encrypted password) don't leak into or break exported files.
    ///
    /// Passwords are deliberately never included: an exported file is meant
    /// to be portable (backed up, moved to another machine, shared for
    /// troubleshooting), and a DPAPI-protected password only decrypts on the
    /// Windows account that created it anyway, so there's nothing safely
    /// portable to include.
    /// </summary>
    public class ExportedSettings
    {
        public int FormatVersion { get; set; } = 1;
        public List<ExportedDeviceConfig> Devices { get; set; } = new List<ExportedDeviceConfig>();
        public int PollingIntervalSeconds { get; set; } = 15;
    }

    public class ExportedDeviceConfig
    {
        public string Name { get; set; }
        public string IpAddress { get; set; }
        public string Protocol { get; set; } = "Legacy";
        public string Email { get; set; }
    }
}
