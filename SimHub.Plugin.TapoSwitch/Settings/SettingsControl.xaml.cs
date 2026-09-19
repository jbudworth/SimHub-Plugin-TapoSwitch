using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimHub.Plugin.TapoSwitch; // for TapoPlugin

namespace SimHub.Plugin.TapoSwitch.Settings
{
    public partial class SettingsControl : UserControl
    {
        private readonly TapoPlugin _plugin;
        private readonly TapoPluginSettings _settings;

        // Designer-only constructor.
        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(TapoPlugin plugin, TapoPluginSettings settings) : this()
        {
            _plugin = plugin;
            _settings = settings;
            DataContext = _settings;
        }

        // Passwords used to be edited with a PasswordBox bound inline inside a
        // DataGridTemplateColumn cell template. That's a known-fragile pattern
        // in WPF: the commit/cancel/focus-loss lifecycle for a PasswordBox
        // inside a DataGrid cell doesn't reliably propagate the typed value
        // back to the bound source, so edits could silently fail to "stick".
        // Instead of patching around that lifecycle, the Password column is
        // now display-only, and this button opens a small standalone dialog
        // with its own PasswordBox and explicit OK/Cancel, which sidesteps
        // the issue entirely.
        private void PasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn && btn.DataContext is TapoDeviceConfig device))
            {
                return;
            }

            var dialog = new PasswordDialog(device.Name, device.Password)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                device.Password = dialog.Password;
                StatusText.Text = $"Password staged for '{device.Name}'. Click Save settings to apply it.";
            }
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            _settings.Devices.Add(new TapoDeviceConfig());
        }

        private void RemoveDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TapoDeviceConfig device)
            {
                var result = MessageBox.Show(
                    $"Remove '{device.Name}'? Any actions bound to it in Controls and Events will stop working " +
                    "until you rebind them to a new switch.",
                    "Remove switch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _settings.Devices.Remove(device);
                }
            }
        }

        private async void TestDevice_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn && btn.DataContext is TapoDeviceConfig device))
            {
                return;
            }

            device.LastStatus = "Testing...";
            try
            {
                bool isOn = await _plugin.TestConnectionAsync(device);
                device.LastStatus = isOn ? "Connected - ON" : "Connected - OFF";
            }
            catch (Exception ex)
            {
                string detail = ex.Message;
                if (ex.InnerException != null) detail += " (" + ex.InnerException.Message + ")";
                device.LastStatus = $"Error: {detail}";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _plugin.SaveSettingsAndRefreshActions();
            StatusText.Text = "Saved. New or renamed switches appear in Controls and Events " +
                "immediately in most SimHub versions; if you don't see them, restart SimHub.";
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export Tapo Smart Switch configuration",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = "SimHub.Plugin.TapoSwitch.json"
            };
            if (dlg.ShowDialog() != true)
            {
                return;
            }

            try
            {
                _plugin.ExportSettings(dlg.FileName);
                MessageBox.Show(
                    "Configuration Exported.\n\nPasswords are not included in exports for security. " +
                    "After import all passwords will need to be re-entered.",
                    "Tapo Smart Switch", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Tapo Smart Switch",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Importing will replace your current switch list. Passwords aren't included in " +
                "exports, so any Klap-protocol switches will need their password re-entered " +
                "afterward. Continue?",
                "Import configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "Import Tapo Smart Switch configuration",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true)
            {
                return;
            }

            try
            {
                int count = _plugin.ImportSettings(dlg.FileName);
                MessageBox.Show(
                    $"Imported {count} switch(es). Click Save settings to apply, then re-enter " +
                    "any Klap account passwords.",
                    "Tapo Smart Switch", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Tapo Smart Switch",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
