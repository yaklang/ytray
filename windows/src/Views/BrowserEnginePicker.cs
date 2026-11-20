#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;

namespace YTray.Views
{
    public sealed class BrowserEnginePicker : ComboBox
    {
        private bool _refreshing;
        private bool _allowAuto = true;
        private BrowserEngineTarget? _remembered;

        public BrowserEnginePicker()
        {
            SetResourceReference(StyleProperty, "YTrayComboBox");
            DropDownOpened += async (_, __) =>
            {
                if (_refreshing) return;
                _refreshing = true;
                try
                {
                    var engines = await BrowserEngineDiscovery.FindAsync(Target ?? _remembered);
                    var selected = Target ?? _remembered;
                    if (IsLoaded) Fill(selected, engines.ToArray());
                }
                catch (Exception error) { DiagnosticLog.Error("browser.engine.discovery", error); }
                finally { _refreshing = false; }
            };
        }

        public BrowserEngineTarget? Target => (SelectedItem as ComboBoxItem)?.Tag as BrowserEngineTarget;

        internal void Configure(BrowserEngineTarget? target, bool allowAuto = true)
        {
            _allowAuto = allowAuto;
            _remembered = target?.Clone();
            Fill(target, Array.Empty<BrowserEngineTarget>());
        }

        private void Fill(BrowserEngineTarget? selected, BrowserEngineTarget[] online)
        {
            Items.Clear();
            if (_allowAuto) Add(new BrowserEngineTarget { Mode = "auto" });
            Add(new BrowserEngineTarget { Mode = "none" });
            foreach (var engine in online)
            {
                var duplicate = online.Count(candidate => candidate.DisplayName == engine.DisplayName) > 1;
                Add(engine, engine.PipeName == null || duplicate ? engine.DisplayName + " · " + engine.Detail : engine.DisplayName);
            }
            if (selected?.Mode == "legacy") Add(selected);
            if (selected?.Mode == "engine" && !online.Any(candidate => SameChoice(candidate, selected)))
                Add(selected, selected.DisplayName + (online.Length == 0 ? "" : " · 等待引擎"));
            SelectedItem = Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Tag is BrowserEngineTarget target && selected != null && SameChoice(target, selected)) ?? Items[0];
        }

        private void Add(BrowserEngineTarget target, string? label = null) =>
            Items.Add(new ComboBoxItem { Content = label ?? target.DisplayName, Tag = target, ToolTip = target.Mode == "engine" ? target.Detail : null });

        private static bool SameChoice(BrowserEngineTarget left, BrowserEngineTarget right) =>
            left.Mode == right.Mode && (left.Mode != "engine" || left.SameFrontend(right) && left.EngineInstanceId == right.EngineInstanceId);
    }
}
