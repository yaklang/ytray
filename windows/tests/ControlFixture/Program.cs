using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace YTray.ControlFixture
{
    internal static class Program
    {
        [STAThread] private static void Main(string[] args)
        {
            if (args.Length == 2 && args[1] is "--drawn-window" or "--drawn-popup") {
                RunDrawnWindow(args[0], args[1] == "--drawn-popup"); return;
            }
            if (args.Length == 0) args = new[] { Path.Combine(Path.GetTempPath(), "ytray-launched-fixture-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json") };
            var captureWindowText = args.Length == 2 && args[1] == "--capture-window-text";
            if (args.Length != 1 && !captureWindowText) return;
            var app = new Application(); var window = new Window { Title = "YTray Control Fixture", Width = 640, Height = 600, Left = 60, Top = 60, Topmost = true };
            var blocker = Path.GetFileName(args[0]).StartsWith("blocker", StringComparison.Ordinal);
            window.Background = blocker ? System.Windows.Media.Brushes.Magenta : System.Windows.Media.Brushes.AliceBlue;
            var panel = new StackPanel { Margin = new Thickness(24) }; window.Content = panel;
            var count = 0; var submissions = 0;
            var windowText = new StringBuilder();
            var increment = new Button { Content = "Increment", Height = 44, Margin = new Thickness(0, 0, 0, 12) }; AutomationProperties.SetAutomationId(increment, "Increment"); panel.Children.Add(increment);
            var countValue = new TextBlock { Text = "Count: 0", FontSize = 20 }; AutomationProperties.SetAutomationId(countValue, "CountValue"); panel.Children.Add(countValue);
            var message = new TextBox { Height = 40, Margin = new Thickness(0, 16, 0, 12), FontSize = 18 }; AutomationProperties.SetName(message, "Message"); AutomationProperties.SetAutomationId(message, "Message"); panel.Children.Add(message);
            var list = new StackPanel(); for (var i = 0; i < 100; i++) list.Children.Add(new TextBlock { Text = "Fixture row " + i, Height = 30 });
            var scroll = new ScrollViewer { Height = 240, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Visible }; AutomationProperties.SetAutomationId(scroll, "FixtureScroll"); panel.Children.Add(scroll);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            object Bounds(FrameworkElement element) { var point = element.PointToScreen(new Point()); return new { x = point.X, y = point.Y, width = element.ActualWidth, height = element.ActualHeight }; }
            string State() => JsonConvert.SerializeObject(new { count, text = message.Text, submissions, scroll = scroll.VerticalOffset, buttonLabel = increment.Content, pid = System.Diagnostics.Process.GetCurrentProcess().Id, port,
                selectionStart = message.SelectionStart, selectionLength = message.SelectionLength, messageBounds = Bounds(message), statusBounds = Bounds(countValue),
                incrementBounds = Bounds(increment), windowText = windowText.ToString() });
            void Save() { try { File.WriteAllText(args[0], State()); } catch { } }
            if (captureWindowText) window.SourceInitialized += (_, __) =>
            {
                var saving = false;
                ((HwndSource)PresentationSource.FromVisual(window)).AddHook((IntPtr handle, int messageId, IntPtr character, IntPtr flags, ref bool handled) =>
                {
                    if (messageId == 0x102) {
                        windowText.Append((char)character.ToInt64());
                        if (!saving) {
                            saving = true;
                            window.Dispatcher.BeginInvoke(new Action(() => { saving = false; Save(); }), DispatcherPriority.ContextIdle);
                        }
                    }
                    return IntPtr.Zero;
                });
            };
            increment.Click += (_, __) => { count++; countValue.Text = "Count: " + count; Save(); };
            window.PreviewKeyDown += (_, e) => { if (e.Key == Key.F2) { increment.Content = "Renamed"; Save(); e.Handled = true; } };
            message.TextChanged += (_, __) => Save(); message.KeyDown += (_, e) => { if (e.Key == Key.Return) { submissions++; window.Title = "YTray Control Fixture · Submitted " + submissions; Save(); } };
            message.SelectionChanged += (_, __) => Save();
            scroll.ScrollChanged += (_, __) => Save(); window.Loaded += (_, __) => Save(); window.LocationChanged += (_, __) => Save(); window.Closed += (_, __) => listener.Stop();
            _ = Task.Run(async () => { while (true) { try { using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream(); var buffer = new byte[2048]; var n = await stream.ReadAsync(buffer, 0, buffer.Length); if (!Encoding.ASCII.GetString(buffer, 0, n).StartsWith("GET /state ", StringComparison.Ordinal)) continue;
                var body = Encoding.UTF8.GetBytes(await window.Dispatcher.InvokeAsync(State)); var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n"); await stream.WriteAsync(header, 0, header.Length); await stream.WriteAsync(body, 0, body.Length);
            } catch { return; } } });
            app.Run(window);
        }

        private static void RunDrawnWindow(string statePath, bool withPopup)
        {
            var app = new Application();
            var surface = new DrawnSurface { Focusable = true };
            var window = new Window {
                Title = "YTray Drawn Fixture", Width = 500, Height = 380, Left = 65, Top = 90,
                Topmost = true, Content = surface
            };
            var popupCount = 0;
            var popupActivations = 0;
            void Save() => File.WriteAllText(statePath, JsonConvert.SerializeObject(new { count = surface.Count, text = surface.Text,
                targetChanged = surface.TargetChanged, decorationChanged = surface.DecorationChanged, caretVisible = surface.CaretVisible,
                popupCount, popupActivations }));
            var changeFile = statePath + ".change-target";
            var decorationFile = statePath + ".change-decoration";
            var caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(480) };
            caretTimer.Tick += (_, __) => { caretTimer.Stop(); surface.ShowCaret(false); };
            var changes = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            changes.Tick += (_, __) => {
                if (Consume(changeFile)) surface.ChangeTarget();
                if (Consume(decorationFile)) surface.ChangeDecoration();
                if (Consume(statePath + ".blink-caret")) { surface.ShowCaret(true); caretTimer.Start(); }
                if (Consume(statePath + ".change-text")) surface.ChangeText();
            };
            bool Consume(string file)
            {
                if (!File.Exists(file)) return false;
                try { File.Delete(file); return true; }
                catch (IOException) { return false; }
            }
            surface.Changed += Save;
            window.Loaded += (_, __) => {
                if (withPopup) {
                    var popupSurface = new DrawnSurface();
                    var popup = new Window {
                        Owner = window, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize, Left = window.Left + 48, Top = window.Top + 80,
                        Width = 360, Height = 250, Content = popupSurface
                    };
                    popup.SourceInitialized += (_, __) => {
                        const int extendedStyle = -20;
                        const int noActivate = 0x08000000;
                        var handle = new WindowInteropHelper(popup).Handle;
                        SetWindowLong(handle, extendedStyle, GetWindowLong(handle, extendedStyle) | noActivate);
                    };
                    popup.Activated += (_, __) => {
                        popupActivations++;
                        Save();
                        popup.Dispatcher.BeginInvoke(new Action(popup.Close), DispatcherPriority.Send);
                    };
                    popupSurface.Changed += () => { popupCount = popupSurface.Count; popup.Close(); Save(); };
                    popup.Show();
                }
                Save();
            };
            window.Closed += (_, __) => { changes.Stop(); caretTimer.Stop(); };
            changes.Start();
            app.Run(window);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        // An application surface with a painted target and no control peers.
        private sealed class DrawnSurface : FrameworkElement
        {
            private readonly Rect _action = new Rect(74, 105, 239, 62);
            private readonly Brush _ready = new SolidColorBrush(Color.FromRgb(44, 109, 174));
            private readonly Brush _clicked = new SolidColorBrush(Color.FromRgb(32, 133, 92));
            private readonly Brush _replacement = new SolidColorBrush(Color.FromRgb(163, 59, 52));
            internal int Count { get; private set; }
            internal string Text { get; private set; } = "";
            internal bool TargetChanged { get; private set; }
            internal bool DecorationChanged { get; private set; }
            internal bool CaretVisible { get; private set; }
            internal event Action? Changed;

            internal void ChangeTarget()
            {
                TargetChanged = true;
                InvalidateVisual();
                Changed?.Invoke();
            }

            internal void ChangeDecoration()
            {
                DecorationChanged = true;
                InvalidateVisual();
                Changed?.Invoke();
            }

            internal void ShowCaret(bool visible)
            {
                CaretVisible = visible;
                InvalidateVisual();
                Changed?.Invoke();
            }

            internal void ChangeText()
            {
                Text += "l";
                InvalidateVisual();
                Changed?.Invoke();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                drawing.DrawRectangle(Brushes.AliceBlue, null, new Rect(RenderSize));
                if (DecorationChanged) drawing.DrawRectangle(Brushes.Gold, null, new Rect(185, 94, 18, 8));
                drawing.DrawRoundedRectangle(TargetChanged ? _replacement : Count == 0 ? _ready : _clicked, null, _action, 8, 8);
                DrawText("Drawn window", new Point(74, 49), Brushes.Black);
                DrawText(TargetChanged ? "Changed target" : Count == 0 ? "Click to focus" : "Focused", new Point(98, 122), Brushes.White);
                if (Text.Length != 0) {
                    var formatted = Format(Text, Brushes.Black);
                    drawing.DrawText(formatted, new Point(74, 200));
                    if (CaretVisible) drawing.DrawRectangle(Brushes.Black, null, new Rect(74 + formatted.Width + 1, 200, 1, 22));
                }

                void DrawText(string text, Point origin, Brush brush) => drawing.DrawText(Format(text, brush), origin);
                FormattedText Format(string text, Brush brush) => new FormattedText(text,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 19, brush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                if (!_action.Contains(e.GetPosition(this))) return;
                Focus();
                Count++;
                InvalidateVisual();
                Changed?.Invoke();
                e.Handled = true;
            }

            protected override void OnTextInput(TextCompositionEventArgs e)
            {
                Text += e.Text;
                InvalidateVisual();
                Changed?.Invoke();
                e.Handled = true;
            }
        }
    }
}
