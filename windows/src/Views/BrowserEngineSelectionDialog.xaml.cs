#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using YTray.Core;

namespace YTray.Views
{
    public partial class BrowserEngineSelectionDialog : Window
    {
        public BrowserEngineSelectionDialog(IReadOnlyList<BrowserEngineTarget> targets)
        {
            InitializeComponent();
            Targets.ItemsSource = targets.Concat(new[] {
                new BrowserEngineTarget { Mode = "legacy" }, new BrowserEngineTarget { Mode = "none" },
            }).ToArray();
            Targets.SelectedIndex = 0;
        }
        public static BrowserEngineTarget? Choose(IReadOnlyList<BrowserEngineTarget> targets)
        {
            var dialog = new BrowserEngineSelectionDialog(targets);
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window.IsVisible);
            if (owner != null) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            return dialog.ShowDialog() == true ? dialog.Targets.SelectedItem as BrowserEngineTarget : null;
        }
        private void Confirm_Click(object sender, RoutedEventArgs e) { if (Targets.SelectedItem != null) DialogResult = true; }
    }
}
