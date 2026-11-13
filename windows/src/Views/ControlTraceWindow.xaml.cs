#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;

namespace YTray.Views
{
    public partial class ControlTraceWindow : Window
    {
        private readonly SystemControlController _controller;
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private JObject[] _saved = Array.Empty<JObject>();
        private JObject? _details;
        private string? _requestedCall;
        private string? _viewedCall;
        private int _refreshQueued;
        private bool _updating, _closed, _savedLoaded, _showTime;
        private enum DetailTab { Readable, Request, Response }
        private DetailTab _detailTab;
        internal string? SelectedCallId => (CallList.SelectedItem as CallRow)?.Id;

        private ControlTraceWindow(SystemControlController controller)
        {
            InitializeComponent();
            MarkSelected(StepModeButton, true);
            _controller = controller;
            _refresh.Tick += (_, __) => {
                _refresh.Stop(); Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_closed) RefreshLists();
            };
            _controller.Trace.Changed += TraceChanged;
            Loaded += async (_, __) => {
                _saved = await controller.Trace.ReadSavedAsync();
                _savedLoaded = true;
                if (!_closed) RefreshLists();
            };
            Closed += (_, __) => { _closed = true; _refresh.Stop(); _controller.Trace.Changed -= TraceChanged; };
        }

        internal static void ShowFor(SystemControlController controller, Window? owner = null, string? callId = null)
        {
            var window = Application.Current.Windows.OfType<ControlTraceWindow>().FirstOrDefault(view => view._controller == controller);
            if (window == null) {
                window = new ControlTraceWindow(controller);
                if (owner?.IsVisible == true) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                window.Show();
            }
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window._requestedCall = callId;
            window._detailTab = DetailTab.Readable;
            window.RefreshLists();
            window.Activate();
        }

        private void TraceChanged()
        {
            if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) _refresh.Start(); }), DispatcherPriority.Background);
        }

        private void RefreshLists()
        {
            var previousRun = (RunList.SelectedItem as RunRow)?.Id;
            var previousCall = SelectedCallId;
            var live = _controller.Trace.Recent();
            var liveIds = new HashSet<string>(live.Select(call => call.Id));
            var calls = live.Select(call => new CallRow(call))
                .Concat(_saved.Where(call => (string?)call["id"] is string id && !liveIds.Contains(id)).Select(call => new CallRow(call)))
                .Where(call => call.TaskId.Length != 0 || call.Tool != "system_status")
                .OrderByDescending(call => call.StartedAt).Take(300).ToArray();
            var runs = calls.GroupBy(call => call.RunId).Select(group => new RunRow(group.Key, group.Reverse().ToArray())).ToArray();
            var requestedRun = runs.FirstOrDefault(run => run.Calls.Any(call => call.Id == _requestedCall));
            var selectedRun = _requestedCall != null ? requestedRun : runs.FirstOrDefault(run => run.Id == previousRun) ?? runs.FirstOrDefault();
            _updating = true;
            RunCountText.Text = runs.Length.ToString(CultureInfo.InvariantCulture);
            RunList.ItemsSource = runs;
            RunList.SelectedItem = selectedRun;
            if (selectedRun != null) RunList.ScrollIntoView(selectedRun);
            SetCalls(selectedRun, requestedRun != null ? _requestedCall : previousCall);
            _updating = false;
            if (requestedRun != null) _requestedCall = null;
            ShowDetails();
            RevealSelectedStep();
        }

        private void SetCalls(RunRow? run, string? selected)
        {
            CallList.ItemsSource = run?.Calls;
            var row = run?.Calls.FirstOrDefault(call => call.Id == selected) ?? run?.Calls.FirstOrDefault();
            CallList.SelectedItem = row;
        }

        private void RevealSelectedStep()
        {
            if (CallList.SelectedItem is not CallRow row) return;
            Dispatcher.BeginInvoke(new Action(() => {
                if (_closed || CallList.SelectedItem != row) return;
                // The timeline may only just have become visible after loading history.
                if (CallList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement item) item.BringIntoView();
                else CallList.ScrollIntoView(row);
            }), DispatcherPriority.Loaded);
        }

        private void Run_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (_updating) return;
            _requestedCall = null;
            _detailTab = DetailTab.Readable;
            _updating = true;
            SetCalls(RunList.SelectedItem as RunRow, null);
            _updating = false;
            ShowDetails();
            RevealSelectedStep();
        }

        private void Call_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (_updating) return;
            _detailTab = DetailTab.Readable;
            ShowDetails();
        }

        private void ShowDetails()
        {
            var row = CallList.SelectedItem as CallRow;
            _details = row?.Snapshot();
            CallDetails.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
            EmptyText.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
            if (row == null || _details == null) {
                EmptyText.Text = !_savedLoaded ? "正在读取调用记录…" : _requestedCall != null ? "未找到这条调用记录" : "等待 MCP 调用";
                return;
            }
            var record = _details;
            if (_viewedCall != row.Id) {
                _viewedCall = row.Id;
                ReadableDetails.ScrollToTop();
                ReturnedTextPanel.IsExpanded = false;
            }
            var presentation = new ControlTraceDetails(record);
            CallPositionText.Text = "第 " + row.Position + " 步";
            ActionTitleText.Text = presentation.Headline.Length == 0 ? row.Label : presentation.Headline;
            ActionTitleText.ToolTip = ActionTitleText.Text;
            CallDurationText.Text = row.Duration.HasValue ? ControlTraceDetails.FormatDuration(row.Duration.Value) : "";
            CallStatusText.Text = row.Status switch {
                "failed" => "失败", "cancelled" => "已取消", "running" => "执行中", _ => "已完成"
            };
            var (statusBackground, statusText) = row.Status switch {
                "completed" => ("SuccessPaleBrush", "SuccessBrush"),
                "failed" => ("DangerPaleBrush", "DangerBrush"),
                "running" => ("BrandPaleBrush", "TextPrimaryBrush"),
                _ => ("SurfaceMutedBrush", "TextSecondaryBrush")
            };
            CallStatusBadge.SetResourceReference(Border.BackgroundProperty, statusBackground);
            CallStatusText.SetResourceReference(TextBlock.ForegroundProperty, statusText);
            ToolNameText.Text = row.Tool;
            ToolNameText.ToolTip = row.Tool;
            var session = row.Session.Length > 0 ? "  ·  会话 " + row.Session.Substring(0, Math.Min(8, row.Session.Length)) : "";
            CallMetadataText.Text = row.StartedAt.ToLocalTime().ToString("HH:mm:ss")
                + (row.ClientName.Length == 0 ? "" : "  ·  " + row.ClientName) + session;
            CallMetadataText.ToolTip = CallMetadataText.Text + (record.Value<int?>("http_status") is int status ? "  ·  HTTP " + status : "")
                + (row.Session.Length == 0 ? "" : "\n" + row.Session);
            CallErrorText.Text = presentation.Error;
            CallErrorPanel.Visibility = CallErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            CallErrorText.ToolTip = CallErrorText.Text;
            InputFields.ItemsSource = presentation.Request;
            ResultFields.ItemsSource = presentation.Response;
            StageList.ItemsSource = presentation.Stages;
            StagePanel.Visibility = presentation.Stages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ReturnedText.Text = presentation.ReturnedText;
            ReturnedTextPanel.Visibility = presentation.ReturnedText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ShowPayload();
            DrawTimingOverview();
        }

        private void ShowPayload()
        {
            var readable = _detailTab == DetailTab.Readable;
            ReadableDetails.Visibility = readable ? Visibility.Visible : Visibility.Collapsed;
            PayloadText.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
            if (!readable) {
                var value = _details?[_detailTab == DetailTab.Response ? "response" : "request"];
                var text = value?.ToString(Formatting.Indented) ?? ((string?)_details?["status"] == "running" ? "等待调用完成" : "该调用没有响应正文");
                if (!string.Equals(PayloadText.Text, text, StringComparison.Ordinal)) PayloadText.Text = text;
            }
            MarkSelected(DetailsButton, readable);
            MarkSelected(RequestButton, _detailTab == DetailTab.Request);
            MarkSelected(ResponseButton, _detailTab == DetailTab.Response);
        }

        private static void MarkSelected(Button button, bool selected)
        {
            if (selected) button.SetResourceReference(BackgroundProperty, "BrandPaleBrush");
            else button.Background = Brushes.Transparent;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void Details_Click(object sender, RoutedEventArgs e) { _detailTab = DetailTab.Readable; ShowPayload(); }
        private void Request_Click(object sender, RoutedEventArgs e) { _detailTab = DetailTab.Request; ShowPayload(); }
        private void Response_Click(object sender, RoutedEventArgs e) { _detailTab = DetailTab.Response; ShowPayload(); }
        private void StepMode_Click(object sender, RoutedEventArgs e) => SetTimeMode(false);
        private void TimeMode_Click(object sender, RoutedEventArgs e) => SetTimeMode(true);
        private void TimeTrack_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) DrawTimingOverview(); }

        private void SetTimeMode(bool showTime)
        {
            _showTime = showTime;
            CallList.Visibility = StepNavigation.Visibility = showTime ? Visibility.Collapsed : Visibility.Visible;
            TimePanel.Visibility = showTime ? Visibility.Visible : Visibility.Collapsed;
            MarkSelected(StepModeButton, !showTime);
            MarkSelected(TimeModeButton, showTime);
            if (showTime) DrawTimingOverview();
            else RevealSelectedStep();
        }

        private void DrawTimingOverview()
        {
            if (!_showTime || TimeTrack.ActualWidth < 8 || RunList.SelectedItem is not RunRow run) return;
            TimeTrack.Children.Clear();
            var rows = run.Calls.Where(row => row.StartedAt != DateTimeOffset.MinValue).ToArray();
            if (rows.Length == 0) { TimeSpanText.Text = "暂无时长数据"; return; }
            var first = rows[0].StartedAt;
            var total = rows.Max(row => (row.StartedAt - first).TotalMilliseconds + (row.Duration ?? 0));
            TimeSpanText.Text = "经过 " + ControlTraceDetails.FormatDuration((long)total);
            var scale = (TimeTrack.ActualWidth - 5) / Math.Max(1, total);
            var lanes = new double[3];
            var usedLanes = 1;
            foreach (var row in rows) {
                var start = (row.StartedAt - first).TotalMilliseconds * scale;
                var span = Math.Max(5, (row.Duration ?? 0) * scale);
                var lane = Array.FindIndex(lanes, end => end <= start);
                if (lane < 0) lane = Array.IndexOf(lanes, lanes.Min());
                lanes[lane] = start + span + 2;
                usedLanes = Math.Max(usedLanes, lane + 1);
                var active = row.Id == SelectedCallId;
                var tooltip = row.Name + " · " + row.StartedAt.ToLocalTime().ToString("HH:mm:ss.fff")
                    + (row.Duration is long elapsed ? " · " + ControlTraceDetails.FormatDuration(elapsed) : " · 执行中");
                var button = new Button { Width = span, Height = 13, Style = (Style)FindResource("TraceTimeSegment"), ToolTip = tooltip,
                    BorderThickness = new Thickness(active ? 1.5 : 0) };
                button.SetResourceReference(BackgroundProperty, row.Failed ? "DangerBrush" : row.Running ? "BrandOrangeBrush" : row.Completed ? "SuccessBrush" : "TextTertiaryBrush");
                button.SetResourceReference(BorderBrushProperty, "TextPrimaryBrush");
                AutomationProperties.SetName(button, tooltip);
                button.Click += (_, __) => CallList.SelectedItem = row;
                Canvas.SetLeft(button, start);
                Canvas.SetTop(button, 2 + lane * 16);
                Panel.SetZIndex(button, active ? 1 : 0);
                TimeTrack.Children.Add(button);
            }
            TimeTrack.Height = 17 + (usedLanes - 1) * 16;
        }
        private void EarlierSteps_Click(object sender, RoutedEventArgs e) => ScrollTimeline(-1);
        private void LaterSteps_Click(object sender, RoutedEventArgs e) => ScrollTimeline(1);

        private void ScrollTimeline(int direction)
        {
            if (FindScrollViewer(CallList) is ScrollViewer viewer)
                viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + direction * Math.Max(108, viewer.ViewportWidth * .55));
        }

        private void Timeline_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (EarlierStepsButton == null || LaterStepsButton == null || e.OriginalSource is not ScrollViewer viewer) return;
            EarlierStepsButton.IsEnabled = viewer.HorizontalOffset > .5;
            LaterStepsButton.IsEnabled = viewer.ScrollableWidth - viewer.HorizontalOffset > .5;
        }

        private void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var viewer = FindScrollViewer(CallList);
            if (viewer == null || viewer.ScrollableWidth <= 0) return;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta * .7);
            e.Handled = true;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer viewer) return viewer;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is ScrollViewer child) return child;
            return null;
        }

        private sealed class RunRow
        {
            internal string Id { get; }
            internal CallRow[] Calls { get; }
            public string Name { get; }
            public string Detail { get; }
            public bool Failed { get; }
            public bool Completed { get; }
            public bool Running { get; }
            internal RunRow(string id, CallRow[] calls)
            {
                Id = id; Calls = calls;
                for (var i = 0; i < calls.Length; i++) {
                    calls[i].Position = i + 1;
                    calls[i].HasNext = i < calls.Length - 1;
                }
                var latest = calls[calls.Length - 1];
                Name = latest.TaskId.Length != 0 ? latest.TaskId : latest.ClientName.Length != 0 ? latest.ClientName
                    : latest.Session.Length == 0 ? "直接连接" : "MCP 会话 " + latest.Session.Substring(0, Math.Min(8, latest.Session.Length));
                Detail = calls.Length + " 次调用  ·  " + latest.StartedAt.ToLocalTime().ToString("MM-dd HH:mm");
                Failed = latest.Failed;
                Completed = latest.Completed;
                Running = latest.Running;
            }
        }

        private sealed class CallRow
        {
            private readonly ControlTraceCall? _live;
            private readonly JObject? _saved;
            internal string Id { get; }
            internal string RunId { get; }
            internal string TaskId { get; }
            internal string Session { get; }
            internal string ClientName { get; }
            internal string Tool { get; }
            internal DateTimeOffset StartedAt { get; }
            internal string Status { get; }
            internal long? Duration { get; }
            public int Position { get; internal set; }
            public bool HasNext { get; internal set; }
            public string Label { get; }
            public string Name => "第 " + Position + " 步 · " + Label;
            public bool Failed => Status == "failed";
            public bool Completed => Status == "completed";
            public bool Running => Status == "running";

            internal CallRow(ControlTraceCall live) : this(live.Id, live.RunId, live.TaskId, live.Session, live.ClientName, live.Tool,
                live.Label, live.StartedAt, live.Status, live.Duration) => _live = live;
            internal CallRow(JObject saved) : this((string?)saved["id"] ?? "", (string?)saved["run_id"] ?? "", (string?)saved["task_id"] ?? "",
                (string?)saved["session"] ?? "", (string?)saved["client"] ?? "", (string?)saved["tool"] ?? "", (string?)saved["label"]
                    ?? ControlOperation.ToolTitle((string?)saved["tool"] ?? "MCP", saved["request"]?["arguments"] as JObject), ParseTime((string?)saved["started_at"]),
                (string?)saved["status"] ?? "", saved.Value<long?>("duration_ms")) => _saved = saved;
            private CallRow(string id, string run, string task, string session, string client, string tool, string label, DateTimeOffset start, string status, long? elapsed)
            {
                Id = id; RunId = run; TaskId = task; Session = session; ClientName = client; Tool = tool; Label = label;
                StartedAt = start; Status = status; Duration = elapsed;
            }
            internal JObject Snapshot() => _live?.Snapshot() ?? _saved!;
            private static DateTimeOffset ParseTime(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time) ? time : DateTimeOffset.MinValue;
        }
    }
}
