using System.Windows;

namespace SimHub.Plugin.TapoSwitch.Settings
{
    /// <summary>
    /// Small standalone dialog for entering a device password.
    ///
    /// This exists because binding a PasswordBox inline inside a
    /// DataGridTemplateColumn's cell template is a known-fragile pattern in
    /// WPF: the commit/cancel/focus-loss lifecycle for that combination
    /// doesn't reliably propagate the typed value back to the bound source,
    /// so edits could silently fail to "stick". A standalone dialog with its
    /// own PasswordBox and explicit OK/Cancel sidesteps that entirely rather
    /// than trying to patch around the DataGrid editing lifecycle.
    /// </summary>
    public partial class PasswordDialog : Window
    {
        public string Password { get; private set; }

        public PasswordDialog(string deviceName, string currentPassword)
        {
            InitializeComponent();
            PromptText.Text = $"Password for '{deviceName}'";
            Pwd.Password = currentPassword ?? string.Empty;
            Pwd.Focus();
            Pwd.SelectAll();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Password = Pwd.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
