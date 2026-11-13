#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Native
{
    internal static class WindowsNativeInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] internal Mouse Mouse; [FieldOffset(0)] internal Keyboard Keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct Mouse { internal int X, Y; internal uint Wheel, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct Keyboard { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
        private static Input Key(ushort key, bool up = false) => new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Key = key, Flags = up ? 2u : 0u } } };
        private static void Send(params Input[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                throw new ControlException("InputFailed", "Windows 未能完整发送输入，请重新观察；必要时使用“修复控制权限”。", "Unknown");
        }
        internal static ushort KeyCode(string input)
        {
            var key = input.Trim().ToLowerInvariant();
            if (key.Length == 1 && key[0] >= 'a' && key[0] <= 'z') return (ushort)char.ToUpperInvariant(key[0]);
            if (key.Length == 1 && key[0] >= '0' && key[0] <= '9') return key[0];
            if (key.StartsWith("f", StringComparison.Ordinal) && int.TryParse(key.Substring(1), out var function) && function >= 1 && function <= 24) return (ushort)(0x70 + function - 1);
            switch (key) {
                case "return": case "enter": return 13; case "tab": return 9; case "escape": case "esc": return 27;
                case "backspace": return 8; case "delete": case "del": return 46; case "insert": return 45;
                case "space": return 32; case "left": case "arrowleft": return 37; case "up": case "arrowup": return 38;
                case "right": case "arrowright": return 39; case "down": case "arrowdown": return 40;
                case "home": return 36; case "end": return 35; case "pageup": case "pgup": return 33; case "pagedown": case "pgdn": return 34;
                case "ctrl": case "control": return 17; case "shift": return 16; case "alt": return 18;
                case "win": case "windows": case "meta": return 91;
                case "plus": case "+": return 0xBB; case "minus": case "-": return 0xBD;
                default: throw new ControlException("InvalidKey", "不支持的按键：" + input);
            }
        }
        internal static void RequireForeground(IntPtr window, WindowsControlTarget? popup = null)
        {
            popup?.RequireVisibleOwned();
            var foreground = GetForegroundWindow();
            if (window != IntPtr.Zero && foreground != window && popup?.IsOwnedBy(foreground) != true)
                throw new ControlException("FocusChanged", "前台窗口已变化，请重新观察后继续");
        }
        internal static void Click(IntPtr window, int x, int y, string button, int count, bool foreground, WaitHandle? cancellation = null,
            bool includeOwnedWindows = true, WindowsControlTarget? popup = null)
        {
            if (foreground) {
                var down = button == "right" ? 8u : button == "middle" ? 32u : 2u;
                for (var i = 0; i < count; i++) {
                    if (i > 0) {
                        if (cancellation == null) Thread.Sleep(70);
                        else if (cancellation.WaitOne(70)) throw ControlCancellation.Error("Unknown");
                    }
                    try { SendPointer(window, x, y, cancellation, includeOwnedWindows, popup, MouseEvent(down), MouseEvent(down << 1)); }
                    catch (ControlException error) when (i > 0 && error.ExecutionState == "NotStarted") {
                        throw new ControlException(error.Code, error.Message, "Unknown");
                    }
                }
                return;
            }
            var point = new WindowsAutomation.NativePoint { X = x, Y = y };
            ScreenToClient(window, ref point);
            var packed = new IntPtr((point.X & 0xffff) | (point.Y << 16));
            var message = button == "right" ? 0x204u : button == "middle" ? 0x207u : 0x201u;
            for (var i = 0; i < count; i++) {
                ControlCancellation.Check(cancellation, i > 0);
                Post(window, message, new IntPtr(button == "right" ? 2 : button == "middle" ? 16 : 1), packed);
                Post(window, message + 1, IntPtr.Zero, packed);
            }
        }
        internal static void Text(IntPtr window, string text, bool foreground, WaitHandle? cancellation = null, bool postCharacters = false)
        {
            ControlCancellation.Check(cancellation);
            if (text.Length > 65536) throw new ControlException("InvalidArguments", "一次输入最多 65536 个字符");
            if (!foreground || postCharacters) {
                var receiver = window;
                if (foreground) {
                    RequireForeground(window);
                    receiver = WindowsGuiFocus.Read(window)?.Window
                        ?? throw new ControlException("FocusChanged", "无法确认字符消息的接收窗口，请重新观察");
                }
                var sentCharacters = false;
                try {
                    for (var index = 0; index < text.Length; index++) {
                        if (foreground) {
                            RequireForeground(window);
                            if (WindowsGuiFocus.Read(window)?.Window != receiver)
                                throw new ControlException("FocusChanged", "字符消息的接收窗口已变化，请重新观察");
                        }
                        ControlCancellation.Check(cancellation, sentCharacters);
                        Post(receiver, 0x102, new IntPtr(text[index]), new IntPtr(1));
                        sentCharacters = true;
                        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                            Post(receiver, 0x102, new IntPtr(text[++index]), new IntPtr(1));
                    }
                } catch (ControlException error) when (sentCharacters && error.ExecutionState == "NotStarted") {
                    throw new ControlException(error.Code, error.Message, "Unknown");
                }
                return;
            }
            var input = new List<Input>(Math.Min(512, text.Length * 2));
            var sent = false;
            void SendTextBatch() {
                RequireForeground(window);
                var batch = input.ToArray();
                ControlCancellation.Check(cancellation, sent);
                Send(batch);
                sent = true;
                input.Clear();
            }
            try {
                for (var index = 0; index < text.Length; index++) {
                    var character = text[index];
                    input.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Scan = character, Flags = 4 } } });
                    input.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Scan = character, Flags = 6 } } });
                    if (input.Count >= 512 && !(char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])))
                        SendTextBatch();
                }
                if (input.Count > 0) SendTextBatch();
            } catch (ControlException error) when (sent && error.ExecutionState == "NotStarted") {
                throw new ControlException(error.Code, error.Message, "Unknown");
            }
        }
        internal static void Press(IntPtr window, string key, IEnumerable<string> modifiers, bool foreground, WaitHandle? cancellation = null)
        {
            var code = KeyCode(key); var held = modifiers.Select(KeyCode).Distinct().ToArray();
            if (!foreground) {
                if (held.Length != 0) throw new ControlException("BackgroundUnavailable", "后台组合键不可用，请使用默认前台模式");
                var scan = MapVirtualKey(code, 0);
                ControlCancellation.Check(cancellation);
                Post(window, 0x100, new IntPtr(code), new IntPtr(1 | (scan << 16)));
                Post(window, 0x101, new IntPtr(code), new IntPtr(unchecked((int)(0xc0000001u | (scan << 16)))));
                return;
            }
            RequireForeground(window);
            var input = new List<Input>(2 + 2 * held.Length);
            foreach (var modifier in held) input.Add(Key(modifier));
            input.Add(Key(code)); input.Add(Key(code, true));
            var release = held.Reverse().Select(modifier => Key(modifier, true)).ToArray();
            input.AddRange(release);
            var batch = input.ToArray();
            ControlCancellation.Check(cancellation);
            try { Send(batch); }
            catch (ControlException) {
                if (release.Length != 0) try { Send(release); } catch (ControlException) { }
                throw;
            }
        }
        internal static void Scroll(IntPtr window, int x, int y, string direction, int amount, bool foreground, WaitHandle? cancellation = null)
        {
            var horizontal = direction == "left" || direction == "right";
            var delta = checked(120 * amount * (direction == "up" || direction == "right" ? 1 : -1));
            if (foreground) {
                SendPointer(window, x, y, cancellation, true, null, MouseEvent(horizontal ? 0x1000u : 0x800u, unchecked((uint)delta)));
            } else {
                var packed = new IntPtr((x & 0xffff) | (y << 16));
                ControlCancellation.Check(cancellation);
                Post(window, horizontal ? 0x20eu : 0x20au, new IntPtr(unchecked(delta << 16)), packed);
            }
        }
        private static Input MouseEvent(uint flags, uint wheel = 0) =>
            new Input { Data = new InputUnion { Mouse = new Mouse { Flags = flags, Wheel = wheel } } };
        internal static void RequirePointerTarget(IntPtr window, WindowsAutomation.NativePoint point, bool includeOwnedWindows = true)
        {
            var root = includeOwnedWindows ? 3u : 2u;
            if (window != IntPtr.Zero && GetAncestor(WindowFromPoint(point), root) != GetAncestor(window, root))
                throw new ControlException("TargetOccluded", "目标位置被其他窗口遮挡，请重新观察");
        }
        private static void SendPointer(IntPtr window, int x, int y, WaitHandle? cancellation, bool includeOwnedWindows, WindowsControlTarget? popup, params Input[] input)
        {
            RequireForeground(window, popup);
            if (popup != null && !WindowsControlTarget.ReadBounds(window).Equals(popup.Bounds))
                throw new ControlException("ViewChanged", "弹层位置已变化，请在新画面中重新选点。");
            var point = new WindowsAutomation.NativePoint { X = x, Y = y };
            RequirePointerTarget(window, point, includeOwnedWindows);
            if (!GetCursorPos(out var original)) throw new ControlException("InputUnavailable", "无法读取当前鼠标位置");
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (!screen.Contains(x, y)) throw new ControlException("InvalidCoordinates", "目标位置不在当前桌面内");
            if (original.X == x && original.Y == y) {
                ControlCancellation.Check(cancellation);
                Send(input); return;
            }

            // Keep the move, action and restoration in one SendInput batch.
            // User mouse input is not interleaved between these events.
            var events = new Input[input.Length + 2];
            events[0] = AbsoluteMove(point, screen);
            Array.Copy(input, 0, events, 1, input.Length);
            events[events.Length - 1] = AbsoluteMove(original, screen);
            ControlCancellation.Check(cancellation);
            Send(events);
            // The normalized Windows coordinates can round by a pixel. Correct
            // that rounding only while the pointer is still at the return point.
            if (GetCursorPos(out var restored) && (restored.X != original.X || restored.Y != original.Y)
                && Math.Abs(restored.X - original.X) <= 1 && Math.Abs(restored.Y - original.Y) <= 1)
                SetCursorPos(original.X, original.Y);
        }
        private static Input AbsoluteMove(WindowsAutomation.NativePoint point, System.Drawing.Rectangle screen)
        {
            int Normalize(int pixel, int origin, int length) =>
                (int)Math.Max(0, Math.Min(65535, ((pixel - origin) * 65535L + length - 2) / Math.Max(1, length - 1)));
            return new Input { Data = new InputUnion { Mouse = new Mouse {
                X = Normalize(point.X, screen.Left, screen.Width), Y = Normalize(point.Y, screen.Top, screen.Height),
                Flags = 0xe001 // MOVE | MOVE_NOCOALESCE | VIRTUALDESK | ABSOLUTE
            } } };
        }
        private static void Post(IntPtr window, uint message, IntPtr key, IntPtr value)
        {
            if (!PostMessage(window, message, key, value)) throw new ControlException("InputFailed", "目标窗口未接受输入", "Unknown");
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] input, int size);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out WindowsAutomation.NativePoint point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(WindowsAutomation.NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref WindowsAutomation.NativePoint point);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr value);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint kind);
    }
}
