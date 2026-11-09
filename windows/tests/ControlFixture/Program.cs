using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json;

namespace YTray.ControlFixture
{
    internal static class Program
    {
        [STAThread] private static void Main(string[] args)
        {
            if (args.Length == 0) args = new[] { Path.Combine(Path.GetTempPath(), "ytray-launched-fixture-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json") };
            if (args.Length != 1) return;
            var app = new Application(); var window = new Window { Title = "YTray Control Fixture", Width = 640, Height = 600, Left = 60, Top = 60, Topmost = true };
            var blocker = Path.GetFileName(args[0]).StartsWith("blocker", StringComparison.Ordinal);
            window.Background = blocker ? System.Windows.Media.Brushes.Magenta : System.Windows.Media.Brushes.AliceBlue;
            var panel = new StackPanel { Margin = new Thickness(24) }; window.Content = panel;
            var count = 0; var submissions = 0;
            var increment = new Button { Content = "Increment", Height = 44, Margin = new Thickness(0, 0, 0, 12) }; AutomationProperties.SetAutomationId(increment, "Increment"); panel.Children.Add(increment);
            var countValue = new TextBlock { Text = "Count: 0", FontSize = 20 }; AutomationProperties.SetAutomationId(countValue, "CountValue"); panel.Children.Add(countValue);
            var message = new TextBox { Height = 40, Margin = new Thickness(0, 16, 0, 12), FontSize = 18 }; AutomationProperties.SetName(message, "Message"); AutomationProperties.SetAutomationId(message, "Message"); panel.Children.Add(message);
            var list = new StackPanel(); for (var i = 0; i < 100; i++) list.Children.Add(new TextBlock { Text = "Fixture row " + i, Height = 30 });
            var scroll = new ScrollViewer { Height = 240, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Visible }; AutomationProperties.SetAutomationId(scroll, "FixtureScroll"); panel.Children.Add(scroll);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            object Bounds(FrameworkElement element) { var point = element.PointToScreen(new Point()); return new { x = point.X, y = point.Y, width = element.ActualWidth, height = element.ActualHeight }; }
            string State() => JsonConvert.SerializeObject(new { count, text = message.Text, submissions, scroll = scroll.VerticalOffset, pid = System.Diagnostics.Process.GetCurrentProcess().Id, port,
                selectionStart = message.SelectionStart, selectionLength = message.SelectionLength, messageBounds = Bounds(message), statusBounds = Bounds(countValue) });
            void Save() { try { File.WriteAllText(args[0], State()); } catch { } }
            increment.Click += (_, __) => { count++; countValue.Text = "Count: " + count; Save(); };
            message.TextChanged += (_, __) => Save(); message.KeyDown += (_, e) => { if (e.Key == Key.Return) { submissions++; window.Title = "YTray Control Fixture · Submitted " + submissions; Save(); } };
            message.SelectionChanged += (_, __) => Save();
            scroll.ScrollChanged += (_, __) => Save(); window.Loaded += (_, __) => Save(); window.Closed += (_, __) => listener.Stop();
            _ = Task.Run(async () => { while (true) { try { using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream(); var buffer = new byte[2048]; var n = await stream.ReadAsync(buffer, 0, buffer.Length); if (!Encoding.ASCII.GetString(buffer, 0, n).StartsWith("GET /state ", StringComparison.Ordinal)) continue;
                var body = Encoding.UTF8.GetBytes(await window.Dispatcher.InvokeAsync(State)); var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n"); await stream.WriteAsync(header, 0, header.Length); await stream.WriteAsync(body, 0, body.Length);
            } catch { return; } } });
            app.Run(window);
        }
    }
}
