using System.Windows;
using System.Windows.Controls;
using YTray.Core;

namespace YTray.Views.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly InstanceStore _store;

        public SettingsPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Load();
        }

        private void Load()
        {
            HomeUrlBox.Text = _store.Settings.HomeURL;
            DebugPortBox.Text = _store.Settings.DebugPort.ToString();
            WebRTCCheck.IsChecked = _store.Settings.RestrictWebRTC;
            NotificationsCheck.IsChecked = _store.Settings.DisableNotifications;
            CertCheck.IsChecked = _store.Settings.IgnoreCertificateErrors;
            FlagsBox.Text = _store.Settings.AdditionalFlags;
        }

        private void Save_Click(object s, RoutedEventArgs e)
        {
            _store.Settings.HomeURL = HomeUrlBox.Text;
            if (int.TryParse(DebugPortBox.Text, out int port)) _store.Settings.DebugPort = port;
            _store.Settings.RestrictWebRTC = WebRTCCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.AdditionalFlags = FlagsBox.Text;
            _store.SaveSettings();
        }
    }
}