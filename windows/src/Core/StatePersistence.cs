using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Newtonsoft.Json;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Persists application state to state.json with ACL restricted to the current user
    /// (mirrors macOS 0600 permissions on state.json).
    /// </summary>
    public static class StatePersistence
    {
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
        };

        public static string StatePath(string applicationDirectory) =>
            Path.Combine(applicationDirectory, "state.json");

        public static PersistedState Load(string applicationDirectory)
        {
            var path = StatePath(applicationDirectory);
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                var state = JsonConvert.DeserializeObject<PersistedState>(json, JsonSettings) ?? new PersistedState();
                if (state.Settings == null) state.Settings = new LaunchSettings();
                return state;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(string applicationDirectory, PersistedState state)
        {
            Directory.CreateDirectory(applicationDirectory);
            var path = StatePath(applicationDirectory);
            var json = JsonConvert.SerializeObject(state, JsonSettings);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            // File.Replace requires an existing destination. The previous implementation
            // silently failed on first launch, so state.json was never created.
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            RestrictToCurrentUser(path);
        }

        /// <summary>Set the file ACL so only the current user account has read/write access.</summary>
        public static void RestrictToCurrentUser(string path)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false); // disable inheritance
                var sid = WindowsIdentity.GetCurrent().User;
                var rule = new FileSystemAccessRule(sid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow);
                security.AddAccessRule(rule);
                fileInfo.SetAccessControl(security);
            }
            catch
            {
                // ACL restriction is best-effort; on some file systems it may not apply.
            }
        }

        /// <summary>The Windows application data directory under %LOCALAPPDATA%\YTray.</summary>
        public static string DefaultApplicationDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTray");
    }
}
