#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.SystemControl;
using YTray.Native;

namespace YTray.Views
{
    public partial class ControlInspectorWindow : Window
    {
        private readonly SystemControlController _controller;
        private readonly List<ControlNode> _nodes = new List<ControlNode>();
        private CancellationTokenSource? _request;
        private ControlInspection? _inspection;
        private ControlNode? _selected;
        private Rect _viewport = Rect.Empty;
        private double _zoom = 1;
        private Vector _pan, _panOrigin;
        private Point? _dragStart;
        private MouseButton _dragButton;
        private ControlNode? _pressedNode;
        private bool _dragged;
        private Window? _layoutDialog;
        private bool _fitPending;
        private bool _settingWindow, _settingTaskCall, _closed;
        private string? _shownTraceId;

        internal ControlInspectorWindow(SystemControlController controller)
        {
            InitializeComponent();
            _controller = controller;
            controller.PropertyChanged += ControllerChanged;
            ThemeManager.ThemeChanged += ThemeChanged;
            SourceInitialized += (_, __) => ApplyTitleBar();
            Loaded += (_, __) => FitWorkingArea();
            Closed += (_, __) => {
                _closed = true; _request?.Cancel();
                controller.PropertyChanged -= ControllerChanged;
                ThemeManager.ThemeChanged -= ThemeChanged;
            };
            RefreshButtons();
        }

        internal static void ShowFor(SystemControlController controller, Window? owner = null, ControlOperation? failure = null)
        {
            var window = Application.Current.Windows.OfType<ControlInspectorWindow>().FirstOrDefault(item => item._controller == controller);
            var created = window == null;
            if (window == null) {
                window = new ControlInspectorWindow(controller);
                if (owner?.IsVisible == true) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                window.Show();
            }
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            if (failure != null) _ = window.InspectOperationAsync(failure);
            else if (created || window.WindowList.Items.Count == 0) _ = window.RefreshWindowsAsync();
        }

        private Task InspectOperationAsync(ControlOperation call)
        {
            var operation = call.Snapshot();
            var error = operation["error"] as JObject;
            var steps = ((JArray)operation["steps"]!).OfType<JObject>().ToArray();
            var details = new List<string>();
            if (error == null) {
                details.Add(SystemControlController.HasOperationFeedback(call.Tool) ? "动作已投递" : "已读取");
                if (SystemControlController.HasOperationFeedback(call.Tool) && steps.Any(step => (string?)step["step"] == "readback"))
                    details.Add("已读取操作后的界面");
            } else {
                details.Add((string?)error["code"] ?? "");
                details.Add((string?)error["execution_state"] switch {
                    "NotStarted" => "动作尚未开始", "Completed" => "动作已投递", _ => "需核对已发生的动作"
                });
                if (error.Value<bool?>("selection_sent") == true) details.Add("已发送全选");
                if (error.Value<bool?>("text_sent") == true) details.Add("已投递文字");
            }
            if (call.Task?.Checkpoint == call) details.Add("断点 #" + call.Id);
            if (call.ResumedFrom is long previous) details.Add("从断点 #" + previous + " 继续");
            var title = error == null ? "调用详情" : "失败阶段 · " + ControlOperation.StepTitle((string?)error["step"]);
            _shownTraceId = call.TraceId;
            ViewTraceButton.Visibility = _shownTraceId == null ? Visibility.Collapsed : Visibility.Visible;
            FailureTitle.Text = title + "  /  " + (string?)operation["description"];
            FailureTitle.SetResourceReference(TextBlock.ForegroundProperty, error == null ? "TextPrimaryBrush" : "DangerBrush");
            FailurePanel.SetResourceReference(Border.BorderBrushProperty, error == null ? "HairlineBrush" : "DangerBrush");
            FailureReason.Text = error == null ? "" : (string?)error["message"] ?? "操作未完成。";
            FailureReason.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            FailureReason.ToolTip = FailureReason.Text;
            FailureOutcome.Text = string.Join("  ·  ", details.Where(value => value.Length != 0));
            FailureSteps.ItemsSource = steps
                .Select(step => new {
                    Label = ((string?)step["status"] switch { "failed" => "× ", "completed" => "✓ ", _ => "· " })
                        + (string?)step["label"] + "  " + step.Value<long>("duration_ms") + " ms",
                    Failed = (string?)step["status"] == "failed"
                }).ToArray();
            SetTaskCalls(call);
            FailurePanel.Visibility = Visibility.Visible;
            ResetInspection();
            return RunAsync(async cancellation => {
                var window = await LoadWindowsAsync(call.Target, call.Desktop, true, cancellation);
                if (window == null) {
                    ShowStatus(call.Target == null && error == null ? "这一步没有关联的应用窗口。请选择窗口查看当前界面。"
                        : "这一步的目标窗口当前不可用。请选择窗口查看当前界面。");
                    return;
                }
                await ReadWindowAsync(window, false, call.ElementToken, cancellation);
            });
        }

        private void SetTaskCalls(ControlOperation call)
        {
            var items = call.Task?.Calls().Select(operation => new TaskCallItem(operation)).ToArray();
            _settingTaskCall = true;
            TaskCalls.ItemsSource = items;
            var selected = items?.FirstOrDefault(item => item.Operation == call);
            TaskCalls.SelectedItem = selected;
            TaskCalls.Visibility = items == null ? Visibility.Collapsed : Visibility.Visible;
            if (selected != null) TaskCalls.ScrollIntoView(selected);
            _settingTaskCall = false;
        }

        private Task RefreshWindowsAsync()
        {
            var selected = WindowList.SelectedItem as WindowItem;
            return RunAsync(async cancellation => {
                await LoadWindowsAsync(selected?.Target, selected?.IsDesktop == true, false, cancellation);
            });
        }

        private async Task<WindowItem?> LoadWindowsAsync(WindowsControlTarget? target, bool desktop, bool exact, CancellationToken cancellation)
        {
            var windows = await _controller.ListInspectionWindowsAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            var items = await Task.Run(() => windows.Select(window => new WindowItem(window, cancellation))
                .OrderBy(window => window.GroupOrder).ThenBy(window => window.App, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToList(), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (_controller.CanInspectDesktop) items.Add(new WindowItem(null));
            var view = new ListCollectionView(items.OrderBy(window => window.GroupOrder).ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WindowItem.Group)));
            var previous = WindowList.SelectedItem as WindowItem;
            var selected = desktop ? items.FirstOrDefault(item => item.IsDesktop)
                : target == null ? null : items.FirstOrDefault(item => SameWindow(item.Target, target));
            if (selected == null && !exact) selected = items.FirstOrDefault();
            _settingWindow = true;
            WindowList.ItemsSource = view;
            WindowList.SelectedItem = selected;
            if (selected != null) WindowList.ScrollIntoView(selected);
            _settingWindow = false;
            if (selected == null || previous == null || !SameWindow(previous.Target, selected.Target)) ResetInspection();
            WindowTitleText.Text = selected?.Title ?? "选择窗口";
            if (items.Count == 0) ShowStatus("当前没有可分析的应用窗口。");
            return selected;
        }

        private static bool SameWindow(WindowsControlTarget? left, WindowsControlTarget? right) =>
            left == null || right == null ? left == right
                : left.Handle == right.Handle && left.Pid == right.Pid && left.Created == right.Created;

        private async Task RunAsync(Func<CancellationToken, Task> work)
        {
            _request?.Cancel();
            using var request = new CancellationTokenSource();
            _request = request;
            ShowStatus("");
            RefreshButtons();
            try { await work(request.Token); }
            catch (Exception) when (request.IsCancellationRequested) { }
            catch (Exception error) { ShowStatus(error.Message, true); }
            finally {
                if (ReferenceEquals(_request, request)) _request = null;
                if (!_closed) RefreshButtons();
            }
        }

        private void ShowStatus(string text, bool error = false)
        {
            if (_closed) return;
            InspectorStatus.Text = text;
            InspectorStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            InspectorStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
        }

        private async Task ReadWindowAsync(WindowItem window, bool screenshot, string? preferred, CancellationToken cancellation)
        {
            ShowStatus("正在读取当前界面…");
            var inspection = await _controller.InspectAsync(window.Target, screenshot, cancellation);
            cancellation.ThrowIfCancellationRequested();
            ShowStatus("");
            PresentInspection(inspection, screenshot, preferred);
        }

        private void PresentInspection(ControlInspection inspection, bool screenshot, string? preferred)
        {
            var keepCamera = _inspection != null && SameWindow(_inspection.Target, inspection.Target) && !_fitPending;
            ResetInspection();
            _inspection = inspection;
            var layout = inspection.Layout;
            _viewport = ReadFrame(layout["window_bounds"] as JObject, "width", "height");
            if (!_viewport.IsEmpty) {
                LayoutWorld.Width = _viewport.Width;
                LayoutWorld.Height = _viewport.Height;
                LayoutWorld.Visibility = Visibility.Visible;
                CanvasControls.IsEnabled = true;
                _fitPending = !keepCamera;
                if (_fitPending) FitViewport(); else ApplyCamera();
            }
            var roots = new List<ControlNode>();
            var byIndex = new Dictionary<int, ControlNode>();
            foreach (var row in (layout["elements"] as JArray ?? new JArray()).OfType<JObject>()) {
                var node = new ControlNode(row);
                if (byIndex.ContainsKey(node.Index)) continue;
                _nodes.Add(node); byIndex.Add(node.Index, node);
                if (row["parent_index"]?.Value<int>() is int parent && parent != node.Index && byIndex.TryGetValue(parent, out var container)) {
                    node.Parent = container; container.Children.Add(node);
                } else { node.Expanded = true; roots.Add(node); }
            }
            var active = preferred != null ? _nodes.FirstOrDefault(node => node.Token == preferred) : null;
            active ??= _nodes.FirstOrDefault(node => node.Data.Value<bool?>("focused") == true);
            ControlTree.ItemsSource = roots;
            if (active != null) SelectNode(active);
            var windowOnly = (string?)layout["layout_status"] == "window_only";
            var source = inspection.Target == null ? "主显示器" : (string?)layout["layout_source"] == "raw" ? "UI Automation 原始视图" : "UI Automation 控件视图";
            ObservationText.Text = source + "  ·  " + _nodes.Count + " 个控件  ·  " + inspection.ObservedAt.ToString("HH:mm:ss")
                + (layout.Value<bool?>("truncated") == true ? "  ·  部分已显示" : "");
            if (windowOnly) ShowStatus("Windows 当前只暴露窗口容器。需要定位画面中的目标时可查看截图。");
            if (screenshot && (string?)inspection.Image?["data"] is string data) {
                using var bytes = new MemoryStream(Convert.FromBase64String(data));
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
                CaptureImage.Source = bitmap;
                ToggleImage(true);
            } else if (screenshot) ShowStatus((string?)layout["screenshot_error"] ?? "当前窗口没有返回可用的截图。", true);
            if (CaptureImage.Visibility != Visibility.Visible) ToggleImage(false);
            RedrawLayout();
        }

        private void ToggleImage(bool show)
        {
            CaptureImage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LayoutCanvas.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            LayoutTitle.Text = show ? "窗口截图" : "窗口布局";
            ScreenshotButton.Content = show ? "返回布局" : "查看截图";
            LayoutPlaceholder.Visibility = show || !_viewport.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ResetInspection()
        {
            _layoutDialog?.Close();
            _inspection = null; _selected = null; _viewport = Rect.Empty; _nodes.Clear();
            _fitPending = false; LayoutWorld.Visibility = Visibility.Collapsed; CanvasControls.IsEnabled = false;
            ControlTree.ItemsSource = null; CaptureImage.Source = null;
            SelectedPanel.Visibility = Visibility.Collapsed;
            ObservationText.Text = "";
            LayoutPlaceholder.Text = "选择窗口后点击「分析」。";
            ToggleImage(false);
            LayoutCanvas.Children.Clear();
        }

        private void SelectNode(ControlNode node)
        {
            for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.Expanded = true;
            if (_selected != null && _selected != node) _selected.Selected = false;
            node.Selected = true;
            ShowNode(node);
            Dispatcher.BeginInvoke(new Action(() => {
                if (_selected != node || _closed) return;
                var chain = new Stack<ControlNode>();
                for (ControlNode? parent = node; parent != null; parent = parent.Parent) chain.Push(parent);
                ItemsControl tree = ControlTree;
                while (chain.Count > 0) {
                    tree.UpdateLayout();
                    if (!(tree.ItemContainerGenerator.ContainerFromItem(chain.Pop()) is TreeViewItem item)) return;
                    item.BringIntoView();
                    tree = item;
                }
            }), DispatcherPriority.Loaded);
        }

        private void ShowNode(ControlNode node)
        {
            _selected = node;
            var data = node.Data;
            SelectedPanel.Visibility = Visibility.Visible;
            SelectedTitle.Text = node.Label;
            var metadata = new List<string>();
            if ((string?)data["automation_id"] is string id && id.Length > 0) metadata.Add("Automation ID " + id);
            if (!node.Frame.IsEmpty) metadata.Add($"屏幕坐标 ({node.Frame.X:0}, {node.Frame.Y:0})  {node.Frame.Width:0} × {node.Frame.Height:0} px");
            if (data.Value<bool?>("enabled") == false) metadata.Add("禁用");
            if (data.Value<bool?>("offscreen") == true) metadata.Add("屏幕外");
            if (data.Value<bool?>("focused") == true) metadata.Add("有焦点");
            else if (data.Value<bool?>("focusable") == true) metadata.Add("可聚焦");
            if (data["patterns"] is JArray patterns && patterns.Count > 0) metadata.Add(string.Join(" / ", patterns.Values<string>()));
            SelectedMetadata.Text = string.Join("  ·  ", metadata);
            var value = (string?)data["value"] ?? "";
            SelectedValue.Text = value; SelectedValue.ToolTip = value;
            SelectedValue.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshButtons();
            RedrawLayout();
        }

        private void RedrawLayout()
        {
            if (LayoutCanvas == null) return;
            LayoutCanvas.Children.Clear();
            if (_viewport.IsEmpty) return;
            void Place(Border border, Rect bounds) {
                border.Width = bounds.Width; border.Height = bounds.Height;
                Canvas.SetLeft(border, bounds.X - _viewport.X);
                Canvas.SetTop(border, bounds.Y - _viewport.Y);
                LayoutCanvas.Children.Add(border);
            }
            var surface = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            surface.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
            surface.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
            Place(surface, _viewport);
            var visible = _nodes.Where(node => node != _selected && node.IsVisible && node.Role is not ("Window" or "Pane" or "Group"))
                .Where(node => _viewport.IntersectsWith(node.Frame))
                .OrderByDescending(node => node.Frame.Width * node.Frame.Height).ToList();
            if (_selected?.IsVisible == true) visible.Add(_selected);
            foreach (var node in visible) {
                var frame = Rect.Intersect(_viewport, node.Frame);
                if (frame.IsEmpty || frame.Width <= 0 || frame.Height <= 0) continue;
                var selected = node == _selected;
                var border = new Border {
                    BorderThickness = new Thickness(selected ? 2 : 1), CornerRadius = new CornerRadius(3), ClipToBounds = true,
                    Background = new SolidColorBrush(Color.FromArgb((byte)(selected ? 40 : 10), 212, 113, 79)),
                    ToolTip = node.Label, Tag = node
                };
                border.SetResourceReference(Border.BorderBrushProperty, selected ? "BrandOrangeBrush" : "HairlineBrush");
                if (frame.Width > 43 && frame.Height > 15) border.Child = new TextBlock {
                    Text = node.Label, FontSize = 12, Margin = new Thickness(4, 1, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis
                };
                border.MouseLeftButtonDown += (_, args) => { args.Handled = true; SelectNode(node); };
                Place(border, frame);
            }
        }

        private Point ViewportCenter => new Point(LayoutViewport.ActualWidth / 2, LayoutViewport.ActualHeight / 2);

        private void FitViewport()
        {
            if (_viewport.IsEmpty || LayoutViewport.ActualWidth < 28 || LayoutViewport.ActualHeight < 28) return;
            var maxZoom = _layoutDialog == null ? 1 : 6;
            _zoom = Math.Min(maxZoom, Math.Min((LayoutViewport.ActualWidth - 24) / _viewport.Width, (LayoutViewport.ActualHeight - 24) / _viewport.Height));
            _pan = new Vector((LayoutViewport.ActualWidth - _viewport.Width * _zoom) / 2,
                (LayoutViewport.ActualHeight - _viewport.Height * _zoom) / 2);
            _fitPending = false;
            ApplyCamera();
        }

        private void ZoomAt(double zoom, Point anchor)
        {
            if (_viewport.IsEmpty) return;
            zoom = Math.Max(Math.Min(.06, _zoom), Math.Min(6, zoom));
            var ratio = zoom / _zoom;
            _pan = new Vector(anchor.X - (anchor.X - _pan.X) * ratio, anchor.Y - (anchor.Y - _pan.Y) * ratio);
            _zoom = zoom;
            _pan = ConstrainPan(_pan);
            _fitPending = false;
            ApplyCamera();
        }

        private void ApplyCamera()
        {
            LayoutWorld.RenderTransform = new MatrixTransform(_zoom, 0, 0, _zoom, _pan.X, _pan.Y);
            ZoomPercentButton.Content = Math.Round(_zoom * 100) + "%";
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs args) => ZoomAt(_zoom * 1.25, ViewportCenter);
        private void ZoomOut_Click(object sender, RoutedEventArgs args) => ZoomAt(_zoom / 1.25, ViewportCenter);
        private void ActualSize_Click(object sender, RoutedEventArgs args) => ZoomAt(1, ViewportCenter);
        private void Fit_Click(object sender, RoutedEventArgs args) => FitViewport();
        private void Viewport_SizeChanged(object sender, SizeChangedEventArgs args) { if (_fitPending) FitViewport(); }

        private void ExpandLayout_Click(object sender, RoutedEventArgs args)
        {
            if (_layoutDialog != null) { _layoutDialog.Close(); return; }
            if (_viewport.IsEmpty || LayoutSlot.Children.Count == 0) return;
            var layout = LayoutSlot.Children[0];
            var savedZoom = _zoom;
            var savedPan = _pan;
            var dialog = new Window {
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowState = WindowState.Maximized,
                Title = WindowTitleText.Text + " · " + LayoutTitle.Text, ShowInTaskbar = false, MinWidth = 420, MinHeight = 260
            };
            dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
            dialog.SourceInitialized += (_, __) => ApplyTitleBar(dialog);
            dialog.PreviewKeyDown += (_, key) => {
                if (key.Key == Key.Escape) { key.Handled = true; dialog.Close(); }
            };
            dialog.ContentRendered += (_, __) => FitViewport();
            _layoutDialog = dialog;
            ExpandLayoutGlyph.Text = "\uE73F";
            ExpandLayoutButton.ToolTip = "退出放大查看（Esc）";
            AutomationProperties.SetName(ExpandLayoutButton, "退出放大查看");
            var host = new Grid { Margin = new Thickness(12) };
            LayoutSlot.Children.Remove(layout);
            host.Children.Add(layout);
            dialog.Content = host;
            _fitPending = true;
            try { dialog.ShowDialog(); }
            finally {
                host.Children.Remove(layout);
                LayoutSlot.Children.Add(layout);
                _layoutDialog = null;
                ExpandLayoutGlyph.Text = "\uE740";
                ExpandLayoutButton.ToolTip = "放大查看布局";
                AutomationProperties.SetName(ExpandLayoutButton, "放大查看布局");
                _fitPending = false;
                LayoutSlot.UpdateLayout();
                _zoom = savedZoom;
                _pan = savedPan;
                ApplyCamera();
            }
        }

        private void Viewport_Wheel(object sender, MouseWheelEventArgs args)
        {
            if (_viewport.IsEmpty) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                ZoomAt(_zoom * Math.Pow(1.18, args.Delta / 120d), args.GetPosition(LayoutViewport));
            else {
                var offset = args.Delta * .45;
                var delta = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? new Vector(offset, 0) : new Vector(0, offset);
                _pan = ConstrainPan(_pan + delta);
                ApplyCamera();
            }
            args.Handled = true;
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs args)
        {
            if (_viewport.IsEmpty || _dragStart.HasValue || args.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
            var select = args.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space);
            if (!LayoutViewport.CaptureMouse()) return;
            _dragStart = args.GetPosition(LayoutViewport);
            _panOrigin = _pan;
            _dragButton = args.ChangedButton;
            _pressedNode = null;
            _dragged = !select;
            for (var visual = args.OriginalSource as Visual; select && visual != null && visual != LayoutViewport; visual = VisualTreeHelper.GetParent(visual) as Visual) {
                if (visual is Border { Tag: ControlNode node }) { _pressedNode = node; break; }
            }
            if (_dragged) LayoutViewport.Cursor = Cursors.ScrollAll;
            args.Handled = true;
        }
        private void Viewport_MouseMove(object sender, MouseEventArgs args)
        {
            if (!_dragStart.HasValue) return;
            var delta = args.GetPosition(LayoutViewport) - _dragStart.Value;
            if (!_dragged && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragged = true;
            LayoutViewport.Cursor = Cursors.ScrollAll;
            _pan = ConstrainPan(_panOrigin + delta);
            ApplyCamera();
            args.Handled = true;
        }
        private void Viewport_MouseUp(object sender, MouseButtonEventArgs args)
        {
            if (!_dragStart.HasValue || args.ChangedButton != _dragButton) return;
            var selected = _dragged ? null : _pressedNode;
            LayoutViewport.ReleaseMouseCapture();
            args.Handled = true;
            if (selected != null) SelectNode(selected);
        }
        private void Viewport_LostCapture(object sender, MouseEventArgs args)
        { _dragStart = null; _pressedNode = null; LayoutViewport.Cursor = Cursors.Hand; }

        private Vector ConstrainPan(Vector pan)
        {
            static double Limit(double offset, double extent, double view) {
                var visible = Math.Min(36, Math.Min(extent, view));
                return Math.Max(visible - extent, Math.Min(view - visible, offset));
            }
            return new Vector(Limit(pan.X, _viewport.Width * _zoom, LayoutViewport.ActualWidth),
                Limit(pan.Y, _viewport.Height * _zoom, LayoutViewport.ActualHeight));
        }

        private static Rect ReadFrame(JObject? frame, string width = "w", string height = "h")
        {
            if (frame == null || frame.Value<double>(width) <= 0 || frame.Value<double>(height) <= 0) return Rect.Empty;
            return new Rect(frame.Value<double>("x"), frame.Value<double>("y"), frame.Value<double>(width), frame.Value<double>(height));
        }

        private void RefreshButtons()
        {
            var idle = _request == null;
            var selected = WindowList.SelectedItem is WindowItem;
            WindowList.IsEnabled = idle;
            RecentFailureButton.IsEnabled = _controller.LatestFailure != null;
            RefreshButton.IsEnabled = idle;
            AnalysisButton.IsEnabled = idle && selected && _controller.IsEnabled;
            ScreenshotButton.IsEnabled = idle && selected && _inspection != null && _controller.IsEnabled;
            HighlightButton.IsEnabled = idle && _inspection?.Target != null && _selected?.IsVisible == true && _selected.Token != null && _controller.IsEnabled;
        }

        private void ControllerChanged(object? sender, PropertyChangedEventArgs args) { if (!_closed) RefreshButtons(); }
        private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshWindowsAsync();
        private async void RecentFailure_Click(object sender, RoutedEventArgs args)
        { if (_controller.LatestFailure is ControlOperation failure) await InspectOperationAsync(failure); }
        private void ViewTrace_Click(object sender, RoutedEventArgs args)
        { if (_shownTraceId != null) ControlTraceWindow.ShowFor(_controller, callId: _shownTraceId); }
        private async void TaskCall_Selected(object sender, SelectionChangedEventArgs args)
        {
            if (!_settingTaskCall && TaskCalls.SelectedItem is TaskCallItem item) await InspectOperationAsync(item.Operation);
        }
        private async void Analyze_Click(object sender, RoutedEventArgs args)
        {
            if (WindowList.SelectedItem is WindowItem window)
                await RunAsync(token => ReadWindowAsync(window, false, _selected?.Token, token));
        }
        private async void Screenshot_Click(object sender, RoutedEventArgs args)
        {
            if (CaptureImage.Visibility == Visibility.Visible) { ToggleImage(false); RedrawLayout(); return; }
            if (WindowList.SelectedItem is WindowItem window)
                await RunAsync(token => ReadWindowAsync(window, true, _selected?.Token, token));
        }
        private async void Highlight_Click(object sender, RoutedEventArgs args)
        {
            if (_inspection?.Target is WindowsControlTarget target && _selected?.Token is string element)
                await RunAsync(token => _controller.HighlightInspectionAsync(target, element, token));
        }
        private void Window_Selected(object sender, SelectionChangedEventArgs args)
        {
            if (_settingWindow || _controller == null) return;
            ResetInspection(); FailurePanel.Visibility = Visibility.Collapsed;
            WindowTitleText.Text = (WindowList.SelectedItem as WindowItem)?.Title ?? "选择窗口";
            ShowStatus(""); RefreshButtons();
        }
        private void Control_Selected(object sender, RoutedPropertyChangedEventArgs<object> args)
        { if (args.NewValue is ControlNode node) ShowNode(node); }

        private void FitWorkingArea()
        {
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var work = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var available = scale.Transform(new Vector(work.Width, work.Height));
            MinWidth = Math.Min(MinWidth, available.X - 20); MinHeight = Math.Min(MinHeight, available.Y - 20);
            MaxWidth = Math.Max(MinWidth, available.X - 20); MaxHeight = Math.Max(MinHeight, available.Y - 20);
            Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
            var origin = scale.Transform(new Point(work.Left, work.Top));
            Left = Math.Max(origin.X + 10, Math.Min(Left, origin.X + available.X - Width - 10));
            Top = Math.Max(origin.Y + 10, Math.Min(Top, origin.Y + available.Y - Height - 10));
        }
        private void ThemeChanged(object? sender, EventArgs args) => ApplyTitleBar();
        private void ApplyTitleBar() => ApplyTitleBar(this);
        private static void ApplyTitleBar(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = ThemeManager.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }

        private sealed class TaskCallItem
        {
            internal ControlOperation Operation { get; }
            public bool Failed { get; }
            public string Name { get; }
            public string Details { get; }

            internal TaskCallItem(ControlOperation operation)
            {
                Operation = operation;
                var summary = operation.Summary();
                var phase = (string?)summary["phase"];
                Failed = phase == "failed";
                var mark = Failed ? "×" : phase == "completed" ? "✓" : "·";
                Name = mark + "  " + operation.TaskPosition + "  " + (string?)summary["label"];
                Details = (string?)summary["description"] ?? "";
                if ((string?)summary["error"]?["message"] is string reason) Details += "\n" + reason;
            }
        }

        private sealed class WindowItem
        {
            internal readonly WindowsControlTarget? Target;
            internal int GroupOrder { get; }
            internal bool IsDesktop => Target == null;
            public string Title => Target?.Title ?? "主显示器";
            public string Name => Title;
            public string App => Target == null ? "Windows 桌面" : Path.GetFileNameWithoutExtension(Target.Executable);
            public string Group => GroupOrder == 0 ? "Windows 原生框架" : GroupOrder == 1 ? "跨平台与自绘界面" : "框架未识别";
            public string Subtitle { get; }
            public string Details { get; }
            public BitmapSource? Icon { get; }
            public Visibility FallbackIconVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
            internal WindowItem(WindowsControlTarget? target, CancellationToken cancellation = default)
            {
                Target = target;
                if (target == null) { Subtitle = "Windows 桌面"; Details = Title; return; }
                var presentation = WindowPresentation.Read(target, cancellation);
                GroupOrder = presentation.Group;
                Icon = presentation.Icon;
                Subtitle = presentation.Framework + "  ·  " + App;
                Details = Title + "\n" + Subtitle + "  ·  PID " + target.Pid
                    + (presentation.Class.Length == 0 ? "" : "\n窗口类 " + presentation.Class);
            }
        }

        private sealed class ControlNode : INotifyPropertyChanged
        {
            internal readonly JObject Data;
            internal readonly Rect Frame;
            internal ControlNode? Parent;
            internal int Index => Data.Value<int>("element_index");
            internal string Role => Data.Value<string>("role") ?? "";
            internal string? Token => Data.Value<string>("element_token");
            internal bool IsVisible => !Frame.IsEmpty && Data.Value<bool?>("offscreen") != true;
            public string Label => "[" + Index + "] " + Role + " " + ((string?)Data["label"] ?? "");
            public string LayerName => (string?)Data["label"] is string name && name.Length != 0 ? name : Role;
            public string LayerNumber => Index.ToString();
            public string LayerIcon => Role switch {
                "Text" => "M 1,2 L 9,2 M 5,2 L 5,10 M 2.5,10 L 7.5,10",
                "Edit" => "M 1,2 L 9,2 9,8 1,8 Z M 4,3 L 4,7 M 3,3 L 5,3 M 3,7 L 5,7",
                "Button" or "CheckBox" or "RadioButton" => "M 1,2 L 9,2 9,8 1,8 Z M 3,5 L 7,5",
                "List" or "ListItem" or "DataGrid" or "Tree" => "M 1,2 L 2,2 M 4,2 L 9,2 M 1,5 L 2,5 M 4,5 L 9,5 M 1,8 L 2,8 M 4,8 L 9,8",
                "Image" => "M 1,1 L 9,1 9,9 1,9 Z M 1,8 L 4,4 6,6 7,5 9,7",
                _ => "M 1,2 L 9,2 9,9 1,9 Z M 1,4 L 9,4"
            };
            public List<ControlNode> Children { get; } = new List<ControlNode>();
            private bool _selected, _expanded;
            public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Notify(nameof(Selected)); } } }
            public bool Expanded { get => _expanded; set { if (_expanded != value) { _expanded = value; Notify(nameof(Expanded)); } } }
            public event PropertyChangedEventHandler? PropertyChanged;
            private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            internal ControlNode(JObject data) { Data = data; Frame = ReadFrame(data["frame"] as JObject); }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
