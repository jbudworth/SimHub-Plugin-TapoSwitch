using System;
using System.ComponentModel;

namespace SimHub.Plugin.TapoSwitch.Settings
{
    /// <summary>Which wire protocol to use for a given device.</summary>
    public enum SwitchProtocol
    {
        /// <summary>Older Kasa/TP-Link plugs (HS100/HS103/HS105/HS110/HS200/HS210, KP1xx/KP3xx,
        /// and any device still on pre-2021 firmware). Plain TCP on port 9999, no login.</summary>
        Legacy,

        /// <summary>Tapo plugs (P100/P105/P110/P115) and Kasa devices updated to KLAP firmware.
        /// Encrypted HTTP on port 80, authenticated with the Tapo/TP-Link account email+password.</summary>
        Klap
    }

    /// <summary>One configured Tapo/Kasa smart switch.</summary>
    public class TapoDeviceConfig : INotifyPropertyChanged
    {
        // Stable id used to build action names. Do not change once actions
        // have been bound in Controls and Events, or existing bindings will
        // stop firing.
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        private string _name = "New Switch";
        public string Name
        {
            get => _name;
            set { _name = value; OnChanged(nameof(Name)); }
        }

        private string _ipAddress = "";
        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnChanged(nameof(IpAddress)); }
        }

        // Default to Legacy: it's the simpler, more common case for older
        // TP-Link/Kasa hardware and needs no credentials to try.
        private SwitchProtocol _protocol = SwitchProtocol.Legacy;
        public SwitchProtocol Protocol
        {
            get => _protocol;
            set { _protocol = value; OnChanged(nameof(Protocol)); OnChanged(nameof(ProtocolName)); }
        }

        // String-typed mirror of Protocol so the settings UI can bind a
        // ComboBox to it without a value converter.
        [Newtonsoft.Json.JsonIgnore]
        public string ProtocolName
        {
            get => Protocol.ToString();
            set
            {
                if (Enum.TryParse<SwitchProtocol>(value, true, out var parsed))
                {
                    Protocol = parsed;
                }
            }
        }

        private string _email = "";
        public string Email
        {
            get => _email;
            set { _email = value; OnChanged(nameof(Email)); }
        }

        // Plaintext, in-memory only - used by the Password dialog and by the
        // Klap client when authenticating. Never written to the settings file
        // directly; see EncryptedPassword below.
        private string _password = "";
        [Newtonsoft.Json.JsonIgnore]
        public string Password
        {
            get => _password;
            set { _password = value; OnChanged(nameof(Password)); OnChanged(nameof(PasswordDisplay)); }
        }

        // Masked, read-only summary shown in the grid (the grid column itself
        // is display-only - editing happens via the row's Password button and
        // dialog instead; see the note on WPF's PasswordBox-in-DataGrid issue
        // in SettingsControl.xaml.cs).
        [Newtonsoft.Json.JsonIgnore]
        public string PasswordDisplay => string.IsNullOrEmpty(_password) ? "(not set)" : new string('\u2022', 8);

        // What actually gets written to/read from SimHub's settings file:
        // the password protected with Windows DPAPI (tied to the Windows
        // user account SimHub runs as), base64-encoded. Encrypting/decrypting
        // happens transparently on serialize/deserialize so nothing else in
        // the plugin needs to know about it.
        public string EncryptedPassword
        {
            get => PasswordProtector.Protect(_password);
            set => _password = PasswordProtector.Unprotect(value);
        }

        // Migration path for settings files saved before encryption was
        // added, which stored the password in plaintext under the "Password"
        // JSON key. Only used on read; never produces output, so it can't
        // resurrect a plaintext "Password" key once a config is re-saved.
        [Newtonsoft.Json.JsonProperty("Password", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        private string LegacyPlainPassword
        {
            get => null;
            set
            {
                if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(_password))
                {
                    _password = value;
                }
            }
        }

        private string _lastStatus = "Unknown";
        [Newtonsoft.Json.JsonIgnore]
        public string LastStatus
        {
            get => _lastStatus;
            set { _lastStatus = value; OnChanged(nameof(LastStatus)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
