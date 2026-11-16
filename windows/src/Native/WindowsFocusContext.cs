#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Newtonsoft.Json.Linq;
using static YTray.Native.WindowsAutomation;

namespace YTray.Native
{
    // A single bounded read-only lane. An unresponsive provider may occupy this
    // lane, but cannot block input or accumulate UIA threads on later snapshots.
    internal static class WindowsFocusContext
    {
        private static readonly object Sync = new object();
        private static Task<JObject>? _pending;
        internal static async Task<JObject> ReadAsync(IntPtr window, Rect? region = null, JObject[]? controls = null)
        {
            JObject Unavailable() => region == null ? new JObject { ["status"] = "unavailable" } : new JObject { ["status"] = "unavailable", ["region_text_unavailable"] = true };
            if (region == null && !IsForeground(window)) return new JObject { ["status"] = "other_window" };
            Task<JObject> task;
            lock (Sync) {
                if (_pending?.IsCompleted == false) return Unavailable();
                task = _pending = Task.Run(() => Read(window, region, controls ?? Array.Empty<JObject>()));
            }
            if (await Task.WhenAny(task, Task.Delay(200)).ConfigureAwait(false) != task) return Unavailable();
            return await task.ConfigureAwait(false);
        }
        private static JObject Read(IntPtr window, Rect? region, JObject[] controls)
        {
            var result = new JObject { ["status"] = "unavailable" };
            try {
                if (IsForeground(window)) ReadFocus(window, result);
            } catch { /* Missing/stale provider data never fails the operation. */ }
            // Do not publish focus from before a user/application focus switch.
            if (!IsForeground(window)) result = new JObject { ["status"] = "other_window" };
            if (region != null) try {
                result["region_covered_controls"] = CoveredControls(window, region.Value, controls);
                result["region_text"] = ReadRegion(window, region.Value, out var truncated);
                if (truncated) result["region_text_truncated"] = true;
            } catch { result["region_text_unavailable"] = true; }
            return result;
        }
        private static void ReadFocus(IntPtr window, JObject result)
        {
            var automation = Create();
            var cache = automation.CreateCacheRequest();
            foreach (var property in new[] { 30001, 30003, 30005, 30019, 30020 }) cache.AddProperty(property);
            var element = automation.GetFocusedElementBuildCache(cache);
            if (element == null || !BelongsTo(automation, element, window)) return;

            result["status"] = "focused";
            var type = ControlType.LookupById(Convert.ToInt32(element.Cached(30003)));
            result["role"] = type?.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal) ?? "Custom";
            result["label"] = element.Cached(30005) as string ?? "";
            if (element.Cached(30001) is double[] frame && frame.Length == 4)
                result["frame"] = Frame(new Rect(frame[0], frame[1], Math.Max(0, frame[2]), Math.Max(0, frame[3])));
            result["selection_state"] = "unavailable";
            // The managed TextPatternRange bridge can fault in UIAutomationCore
            // when reading WPF selections. Use the native UIA client for the range.
            if (element.Cached(30019) is not false || element.Pattern(10014) is not ITextPattern pattern || pattern.SupportedTextSelection() == 0)
                return;
            var ranges = pattern.GetSelection();
            var count = ranges?.Length() ?? 0;
            if (ranges == null || count == 0) return;
            var text = string.Join(" | ", Enumerable.Range(0, Math.Min(count, 2))
                .Select(i => ranges.GetElement(i)?.GetText(161))
                .Where(value => !string.IsNullOrEmpty(value)));
            result["selection_state"] = text.Length == 0 ? "none" : "selected";
            if (text.Length != 0) {
                result["selected_text"] = Clip(text, 160);
                result["selection_truncated"] = text.Length > 160 || count > 2;
            }
        }
        private static JArray CoveredControls(IntPtr window, Rect region, JObject[] controls)
        {
            var covered = new JArray();
            if (!IsForeground(window)) return covered;
            var clock = Stopwatch.StartNew(); var count = 0;
            var cache = new CacheRequest(); cache.Add(AutomationElement.BoundingRectangleProperty); cache.Add(AutomationElement.ControlTypeProperty); cache.Add(AutomationElement.NameProperty);
            var hits = new List<(Rect Bounds, string Role, string Name)>();
            bool Interactive(string role) => role is "Edit" or "Button" or "Hyperlink" or "ComboBox";
            foreach (var control in controls.OrderBy(c => (string?)c["role"] is "Edit" or "ComboBox" ? 0 : (string?)c["role"] == "Button" ? 1 : 2)) {
                if (!Core.SystemControl.ControlRegion.TryFrame(control["frame"] as JObject, out var frame) || !frame.IntersectsWith(region) || control["element_index"] == null) continue;
                var role = (string?)control["role"] ?? "";
                if (role.Equals("Document", StringComparison.OrdinalIgnoreCase) || role.Equals("Window", StringComparison.OrdinalIgnoreCase) || role.Equals("Pane", StringComparison.OrdinalIgnoreCase)) continue;
                // Providers can hit-test an enclosing Edit instead of one of its
                // buttons. Preserve named controls; prune only covered text and
                // raw-URL aliases, whose removal cannot hide the main action.
                var alias = role == "Hyperlink" && Uri.TryCreate((string?)control["label"], UriKind.Absolute, out var labelUri)
                    && (labelUri.Scheme == "http" || labelUri.Scheme == "https")
                    && controls.Any(c => (string?)c["role"] == "Hyperlink" && (string?)c["value"] == (string?)control["value"]
                        && !string.IsNullOrWhiteSpace((string?)c["label"]) && !Uri.TryCreate((string?)c["label"], UriKind.Absolute, out _));
                var mayOmit = role == "Text" || alias;
                var intersection = Rect.Intersect(frame, region); var point = new Point(intersection.X + intersection.Width / 2, intersection.Y + intersection.Height / 2);
                bool Same((Rect Bounds, string Role, string Name) hit) => hit.Role.Equals(role, StringComparison.OrdinalIgnoreCase)
                    && (Math.Abs(hit.Bounds.X - frame.X) <= 3 && Math.Abs(hit.Bounds.Y - frame.Y) <= 3 && Math.Abs(hit.Bounds.Width - frame.Width) <= 3 && Math.Abs(hit.Bounds.Height - frame.Height) <= 3
                        || hit.Name == (string?)control["label"] && hit.Bounds.Contains(point));
                if (hits.Any(Same)) continue;
                var occluder = hits.Where(h => h.Bounds.Contains(point)).OrderBy(h => h.Bounds.Width * h.Bounds.Height).FirstOrDefault();
                if (mayOmit && occluder.Role != null && (!Interactive(role) || !occluder.Bounds.Contains(frame))) {
                    covered.Add(control["element_index"]!.DeepClone()); continue;
                }
                if (clock.ElapsedMilliseconds >= 60 || count >= 24) continue;
                // A desktop-level hit test is meaningful only when this app owns
                // the point. Background/occluded-window reads remain available.
                if (GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2) != GetAncestor(window, 2)) continue;
                count++;
                try {
                    var current = AutomationElement.FromPoint(point).GetUpdatedCache(cache).Cached;
                    var hit = (Bounds: current.BoundingRectangle, Role: current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal), Name: current.Name);
                    if (!Interactive(hit.Role)) continue;
                    hits.Add(hit);
                    // Confirmed foreground controls can cover old document boxes
                    // under sticky headers. Never guess when the hit is ambiguous.
                    if (mayOmit && !Same(hit)) covered.Add(control["element_index"]!.DeepClone());
                } catch { }
            }
            return covered;
        }
        private static bool BelongsTo(IAutomation automation, IElement focused, IntPtr window)
        {
            var root = GetAncestor(window, 2);
            var walker = automation.RawViewWalker();
            IElement? element = focused;
            for (var depth = 0; element != null && depth < 32; depth++) {
                var handle = depth == 0 ? element.Cached(30020) : element.Current(30020);
                if (handle is int hwnd && hwnd != 0) return GetAncestor(new IntPtr(hwnd), 2) == root;
                element = walker.Parent(element);
            }
            return false;
        }
        private static JArray ReadRegion(IntPtr window, Rect region, out bool truncated)
        {
            truncated = false;
            var texts = new JArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<AutomationElement>(); pending.Push(AutomationElement.FromHandle(window));
            var clock = Stopwatch.StartNew(); var visited = 0; var budget = 3600;
            while (pending.Count > 0 && visited++ < 160 && texts.Count < 32 && clock.ElapsedMilliseconds < 100) {
                var element = pending.Pop(); var current = element.Current;
                if (current.IsOffscreen || !region.IntersectsWith(current.BoundingRectangle)) continue;
                if (current.ControlType == ControlType.Text && !current.IsPassword && !string.IsNullOrWhiteSpace(current.Name) && names.Add(current.Name)) {
                    var text = Clip(current.Name, Math.Min(600, budget));
                    if (text.Length < current.Name.Length) truncated = true;
                    if (text.Length > 0) { texts.Add(new JObject { ["text"] = text, ["frame"] = Frame(current.BoundingRectangle) }); budget -= text.Length; }
                    if (budget <= 0) { truncated = true; break; }
                }
                var children = new List<AutomationElement>();
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                while (child != null && children.Count < 160 && clock.ElapsedMilliseconds < 100) { children.Add(child); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
                if (child != null) truncated = true;
                for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
            truncated |= pending.Count > 0; return texts;
        }
        private static string Clip(string text, int limit)
        {
            if (text.Length <= limit) return text;
            if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
            return text.Substring(0, limit);
        }
        private static JObject Frame(Rect rect) => new JObject { ["x"] = rect.X, ["y"] = rect.Y, ["w"] = rect.Width, ["h"] = rect.Height };
        private static bool IsForeground(IntPtr window) => GetAncestor(GetForegroundWindow(), 2) == GetAncestor(window, 2);
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    }
}
