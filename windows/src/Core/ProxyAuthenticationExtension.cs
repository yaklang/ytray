using System;
using System.IO;
using Newtonsoft.Json;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Generates a temporary MV3 extension that responds to proxy 407 challenges,
    /// mirroring macOS ProxyAuthenticationExtension. Files are ACL-restricted to the current user.
    /// </summary>
    public static class ProxyAuthenticationExtension
    {
        public static string Write(Guid instanceId, string username, string password, string applicationDirectory)
        {
            if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password)) return null;
            var dir = ExtensionURL(instanceId, applicationDirectory);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            RestrictToCurrentUser(dir);

            var manifest = new
            {
                manifest_version = 3,
                name = "YTray Proxy Authentication",
                version = "1.0.0",
                permissions = new[] { "webRequest", "webRequestAuthProvider" },
                host_permissions = new[] { "<all_urls>" },
                background = new { service_worker = "background.js" },
            };
            var usernameLiteral = JsonConvert.SerializeObject(username ?? "");
            var passwordLiteral = JsonConvert.SerializeObject(password ?? "");
            var manifestJson = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            var script = $@"
const username = {usernameLiteral};
const password = {passwordLiteral};
const attempts = new Map();

chrome.webRequest.onAuthRequired.addListener(
  (details, callback) => {{
    if (!details.isProxy) {{ callback({{}}); return; }}
    const count = (attempts.get(details.requestId) || 0) + 1;
    attempts.set(details.requestId, count);
    if (count > 1) {{ callback({{ cancel: true }}); return; }}
    callback({{ authCredentials: {{ username, password }} }});
  }},
  {{ urls: [""<all_urls>""] }},
  [""asyncBlocking""]
);

const clearAttempt = details => attempts.delete(details.requestId);
chrome.webRequest.onCompleted.addListener(clearAttempt, {{ urls: [""<all_urls>""] }});
chrome.webRequest.onErrorOccurred.addListener(clearAttempt, {{ urls: [""<all_urls>""] }});
";
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);
            File.WriteAllText(Path.Combine(dir, "background.js"), script);
            RestrictToCurrentUser(Path.Combine(dir, "manifest.json"));
            RestrictToCurrentUser(Path.Combine(dir, "background.js"));
            return dir;
        }

        public static void Remove(Guid instanceId, string applicationDirectory)
        {
            try { var d = ExtensionURL(instanceId, applicationDirectory); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        public static string ExtensionURL(Guid instanceId, string applicationDirectory) =>
            Path.Combine(applicationDirectory, "ProxyAuth", instanceId.ToString());

        private static void RestrictToCurrentUser(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var security = new System.Security.AccessControl.FileSecurity();
                    security.SetAccessRuleProtection(true, false);
                    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                    new FileInfo(path).SetAccessControl(security);
                }
                else if (Directory.Exists(path))
                {
                    var security = new System.Security.AccessControl.DirectorySecurity();
                    security.SetAccessRuleProtection(true, false);
                    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                    new DirectoryInfo(path).SetAccessControl(security);
                }
            }
            catch { }
        }
    }
}