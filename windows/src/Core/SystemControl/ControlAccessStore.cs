#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    // The native master switch owns this credential. Runtime restarts and Windows
    // unlock reuse it; switching off revokes it. DPAPI binds it to this Windows user.
    internal sealed class ControlAccessStore
    {
        private readonly string _directory;
        internal string Path => System.IO.Path.Combine(_directory, "access.bin");
        internal ControlAccessStore(string applicationDirectory) { _directory = System.IO.Path.Combine(applicationDirectory, "ComputerControl"); }
        internal string Open(bool restore)
        {
            PrivateDirectory(_directory);
            if (restore && File.Exists(Path)) {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path), null, DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(bytes);
                if (Convert.FromBase64String(token).Length != 32) throw new IOException("电脑控制配置已损坏，请关闭后重新开启。");
                return token;
            }
            var secret = ControlJson.Secret();
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            var staging = Path + "." + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(staging, encrypted); if (File.Exists(Path)) File.Replace(staging, Path, null); else File.Move(staging, Path); }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            return secret;
        }
        internal void Clear() { if (File.Exists(Path)) File.Delete(Path); }
        internal static void PrivateDirectory(string path)
        {
            for (var directory = new DirectoryInfo(System.IO.Path.GetFullPath(path)); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("控制配置目录不能包含重解析点");
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法识别当前用户");
            security.SetOwner(user); security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
    }
}
