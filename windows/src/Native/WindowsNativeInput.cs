#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
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
        internal static void RequireForeground(IntPtr window)
        {
            if (window != IntPtr.Zero && GetForegroundWindow() != window)
                throw new ControlException("FocusChanged", "前台窗口已变化，请重新观察后继续");
        }
        internal static void Click(IntPtr window, int x, int y, string button, int count, bool foreground)
        {
            if (foreground) {
                RequireForeground(window);
                if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var down = button == "right" ? 8u : button == "middle" ? 32u : 2u;
                for (var i = 0; i < count; i++) { Send(new Input { Data = new InputUnion { Mouse = new Mouse { Flags = down } } }, new Input { Data = new InputUnion { Mouse = new Mouse { Flags = down << 1 } } }); if (count > 1) Thread.Sleep(70); }
                return;
            }
            var point = new WindowsAutomation.NativePoint { X = x, Y = y };
            ScreenToClient(window, ref point);
            var packed = new IntPtr((point.X & 0xffff) | (point.Y << 16));
            var message = button == "right" ? 0x204u : button == "middle" ? 0x207u : 0x201u;
            for (var i = 0; i < count; i++) { Post(window, message, new IntPtr(button == "right" ? 2 : button == "middle" ? 16 : 1), packed); Post(window, message + 1, IntPtr.Zero, packed); }
        }
        internal static void Text(IntPtr window, string text, bool foreground)
        {
            if (text.Length > 65536) throw new ControlException("InvalidArguments", "一次输入最多 65536 个字符");
            if (!foreground) { foreach (var character in text) Post(window, 0x102, new IntPtr(character), new IntPtr(1)); return; }
            RequireForeground(window);
            var input = new List<Input>(Math.Min(512, text.Length * 2));
            var sent = false;
            try { foreach (var character in text) {
                input.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Scan = character, Flags = 4 } } });
                input.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Scan = character, Flags = 6 } } });
                if (input.Count >= 512) { RequireForeground(window); Send(input.ToArray()); sent = true; input.Clear(); }
            }
            if (input.Count > 0) { RequireForeground(window); Send(input.ToArray()); }
            } catch (ControlException error) when (sent && error.ExecutionState != "Unknown") { throw new ControlException(error.Code, error.Message, "Unknown"); }
        }
        internal static void Press(IntPtr window, string key, IEnumerable<string> modifiers, bool foreground)
        {
            var code = KeyCode(key); var held = modifiers.Select(KeyCode).Distinct().ToArray();
            if (!foreground) {
                if (held.Length != 0) throw new ControlException("BackgroundUnavailable", "后台组合键不可用，请使用默认前台模式");
                var scan = MapVirtualKey(code, 0);
                Post(window, 0x100, new IntPtr(code), new IntPtr(1 | (scan << 16)));
                Post(window, 0x101, new IntPtr(code), new IntPtr(unchecked((int)(0xc0000001u | (scan << 16)))));
                return;
            }
            RequireForeground(window);
            try { foreach (var modifier in held) Send(Key(modifier)); Send(Key(code), Key(code, true)); }
            finally { foreach (var modifier in held.Reverse()) Send(Key(modifier, true)); }
        }
        internal static void Scroll(IntPtr window, int x, int y, string direction, int amount, bool foreground)
        {
            var horizontal = direction == "left" || direction == "right";
            var delta = checked(120 * amount * (direction == "up" || direction == "right" ? 1 : -1));
            if (foreground) {
                RequireForeground(window); SetCursorPos(x, y);
                Send(new Input { Data = new InputUnion { Mouse = new Mouse { Flags = horizontal ? 0x1000u : 0x800u, Wheel = unchecked((uint)delta) } } });
            } else {
                var packed = new IntPtr((x & 0xffff) | (y << 16));
                Post(window, horizontal ? 0x20eu : 0x20au, new IntPtr(unchecked(delta << 16)), packed);
            }
        }
        private static void Post(IntPtr window, uint message, IntPtr key, IntPtr value)
        {
            if (!PostMessage(window, message, key, value)) throw new ControlException("InputFailed", "目标窗口未接受输入", "Unknown");
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] input, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref WindowsAutomation.NativePoint point);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr value);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint kind);
    }
}
