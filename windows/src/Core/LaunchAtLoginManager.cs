#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace YTray.Core
{
    public interface ILaunchAtLoginBackend
    {
        bool IsEnabled { get; }
        void Enable();
        void Disable();
    }

    public sealed class WindowsLaunchAtLoginBackend : ILaunchAtLoginBackend
    {
        internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "YTray";

        private static string ExecutablePath =>
            Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法定位 YTray 可执行文件。");

        private static string StartupCommand => $"\"{ExecutablePath}\" --startup";

        public bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
                {
                    var command = key?.GetValue(ValueName) as string;
                    return string.Equals(command, StartupCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public void Enable()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
            {
                if (key == null) throw new InvalidOperationException("无法打开当前用户启动项注册表。 ");
                key.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
            }
        }

        public void Disable()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }

    public enum FirstLaunchAtLoginOutcome
    {
        None,
        Enabled,
        Failed,
    }

    public sealed class LaunchAtLoginManager : INotifyPropertyChanged
    {
        private readonly ILaunchAtLoginBackend _backend;
        private bool _isEnabled;
        private string? _errorMessage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public LaunchAtLoginManager(ILaunchAtLoginBackend? backend = null)
        {
            _backend = backend ?? new WindowsLaunchAtLoginBackend();
            Refresh();
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            private set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(StatusTitle));
                OnPropertyChanged(nameof(StatusDetail));
            }
        }

        public string StatusTitle => IsEnabled ? "已开启" : "未开启";
        public string StatusDetail => IsEnabled
            ? "登录 Windows 后，YTray 会自动进入系统托盘，不会自动打开浏览器实例。"
            : "YTray 不会随登录自动运行；你仍可随时手动打开应用。";

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_errorMessage == value) return;
                _errorMessage = value;
                OnPropertyChanged(nameof(ErrorMessage));
            }
        }

        public bool SetEnabled(bool enabled)
        {
            ErrorMessage = null;
            try
            {
                if (enabled) _backend.Enable();
                else _backend.Disable();
                Refresh();
                if (IsEnabled != enabled)
                    throw new InvalidOperationException("系统没有保存启动项设置。");
                return true;
            }
            catch (Exception ex)
            {
                Refresh();
                ErrorMessage = (enabled ? "无法开启开机启动：" : "无法关闭开机启动：") + ex.Message;
                return false;
            }
        }

        public void Refresh()
        {
            try
            {
                IsEnabled = _backend.IsEnabled;
            }
            catch (Exception ex)
            {
                IsEnabled = false;
                ErrorMessage = "无法读取开机启动状态：" + ex.Message;
            }
        }

        public FirstLaunchAtLoginOutcome EnableOnFirstLaunchIfNeeded(
            Models.LaunchSettings settings,
            Action saveSettings)
        {
            if (settings.LaunchAtLoginSetupCompleted) return FirstLaunchAtLoginOutcome.None;
            settings.LaunchAtLoginSetupCompleted = true;
            saveSettings();
            return SetEnabled(true) ? FirstLaunchAtLoginOutcome.Enabled : FirstLaunchAtLoginOutcome.Failed;
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class PreviewLaunchAtLoginBackend : ILaunchAtLoginBackend
    {
        public bool IsEnabled { get; private set; } = true;
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }
}
