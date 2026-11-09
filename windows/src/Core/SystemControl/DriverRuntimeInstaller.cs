#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    internal sealed class DriverRuntimeInstaller
    {
        internal static JObject Manifest {
            get { using var stream = typeof(DriverRuntimeInstaller).Assembly.GetManifestResourceStream("YTray.CuaDriverManifest") ?? throw new IOException("Driver manifest missing");
                using var reader = new StreamReader(stream); return JObject.Parse(reader.ReadToEnd()); }
        }
        private readonly string _root;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct SystemInfo { internal ushort Architecture, Reserved; internal uint PageSize; internal IntPtr Minimum, Maximum, Mask; internal uint Count, Type, Granularity; internal ushort Level, Revision; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out SystemInfo system);
        internal DriverRuntimeInstaller(string applicationDirectory) { _root = Path.Combine(applicationDirectory, "Runtimes", "CuaDriver", "0.38.0", "windows-x86_64"); }
        internal string Executable => Path.Combine(_root, "cua-driver.exe");
        internal bool Installed { get { try { Verify(); return true; } catch { return false; } } }
        internal static void PrivateDirectory(string path)
        {
            var absolute = Path.GetFullPath(path);
            for (var current = new DirectoryInfo(absolute); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("运行时目录不能包含重解析点");
            Directory.CreateDirectory(absolute);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法核验当前用户");
            security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(absolute).SetAccessControl(security);
        }
        internal static string Hash(Stream stream) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        internal void Verify()
        {
            GetNativeSystemInfo(out var system);
            if (system.Architecture != 9) throw new ControlException("ArchitectureUnsupported", "当前电脑控制组件支持 Windows x64；ARM64 与 32 位系统尚未启用");
            PrivateDirectory(_root);
            foreach (var file in ((JObject)Manifest["files"]!).Properties()) {
                var path = Path.Combine(_root, file.Name);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Driver 文件被替换");
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Hash(stream) != file.Value.Value<string>()) throw new ControlException("RuntimeDamaged", "Driver 摘要校验失败，请重新安装");
            }
        }
        internal async Task InstallAsync(CancellationToken cancellation)
        {
            var staging = _root + ".staging-" + Guid.NewGuid().ToString("N"); PrivateDirectory(staging);
            try {
                var archive = Path.Combine(staging, "driver.zip");
                using (var output = File.Create(archive)) {
                    using var bundled = typeof(DriverRuntimeInstaller).Assembly.GetManifestResourceStream("YTray.CuaDriverArchive");
                    if (bundled != null) await bundled.CopyToAsync(output, 81920, cancellation).ConfigureAwait(false);
                    else {
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                        using var response = await http.GetAsync((string)Manifest["url"]!, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode(); using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        var buffer = new byte[81920]; int count; long total = 0;
                        while ((count = await source.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0) {
                            total += count; if (total > Manifest["archive_size"]!.Value<long>()) throw new IOException("Driver 下载超限");
                            await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                        }
                    }
                }
                using (var input = File.OpenRead(archive)) if (input.Length != Manifest["archive_size"]!.Value<long>() || Hash(input) != (string?)Manifest["archive_sha256"]) throw new IOException("Driver archive digest mismatch");
                using (var zip = ZipFile.OpenRead(archive)) {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                    var files = (JObject)Manifest["files"]!;
                    foreach (var entry in zip.Entries) {
                        var parts = entry.FullName.Replace('\\', '/').Split('/');
                        if (parts.Any(p => p == ".." || p.Contains(":")) || entry.FullName.StartsWith("/") || !seen.Add(entry.FullName)) throw new IOException("Invalid Driver ZIP path");
                        total += entry.Length; if (total > 160L * 1024 * 1024 || zip.Entries.Count > 32) throw new IOException("Driver ZIP exceeds limits");
                        if (files[entry.Name] == null) continue;
                        entry.ExtractToFile(Path.Combine(staging, entry.Name));
                    }
                }
                File.Delete(archive);
                foreach (var file in ((JObject)Manifest["files"]!).Properties()) { using var input = File.OpenRead(Path.Combine(staging, file.Name)); if (Hash(input) != (string?)file.Value) throw new IOException("Driver file digest mismatch"); }
                cancellation.ThrowIfCancellationRequested();
                // Versions are immutable. Move an existing damaged/incomplete version aside;
                // the Controller only permits installation while stopped.
                var old = _root + ".previous-" + Guid.NewGuid().ToString("N");
                if (Directory.Exists(_root)) Directory.Move(_root, old);
                try { Directory.Move(staging, _root); } catch { if (Directory.Exists(old)) Directory.Move(old, _root); throw; }
                if (Directory.Exists(old)) Directory.Delete(old, true);
                Verify();
            } finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }
}
