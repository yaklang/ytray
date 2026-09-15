#nullable enable
using System.Linq;
using System.Windows;
using YTray.Core;

namespace YTray.Views
{
    public partial class ExtensionLaunchDialog : Window
    {
        public ExtensionLaunchDialog(ExtensionLaunchPrompt prompt)
        {
            InitializeComponent();
            MessageScroll.MaxHeight = System.Math.Max(100, SystemParameters.WorkArea.Height - 180);
            MessageText.Text = prompt.Message;
            ContinueButton.Visibility = prompt.CanSkipPlugins ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (s, e) => CancelButton.Focus();
        }

        public static bool Confirm(ExtensionLaunchPrompt prompt)
        {
            var dialog = new ExtensionLaunchDialog(prompt);
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            return dialog.ShowDialog() == true;
        }

        private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
