using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YTray.Models;
using YTray.Native;

namespace YTray.Core
{
    /// <summary>
    /// Resolves the AppUserModelID for a launched Chrome instance using the robust strategy
    /// requested by the user:
    ///   1. Read the base AUMID from the installed Chrome shortcut (.lnk), or fall back to kind default.
    ///   2. After launch, read the real AUMID from Chrome's first top-level window.
    ///   3. Cross-check against the Chromium profile-ID rule (base + "." + profile_id).
    ///   4. Persist the resolved value into the instance metadata.
    /// </summary>
    public static class AumidResolver
    {
        /// <summary>Default Chrome user-data directory, used to detect the "default profile" case.</summary>
        public static string DefaultChromeUserDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data");

        /// <summary>
        /// Compute the profile-id component per the Chromium rule:
        ///   profile_path = user_data_dir + "\" + profile_dir (default "Default")
        ///   profile_id  = parent_basename + "." + profile_basename, keeping only [A-Za-z0-9.]
        /// The default profile (&lt;default_user_data_dir&gt;\Default) yields an empty profile_id.
        /// </summary>
        public static string ComputeProfileId(string profilePath)
        {
            if (string.IsNullOrEmpty(profilePath)) return "";
            var full = Path.GetFullPath(profilePath.TrimEnd(Path.DirectorySeparatorChar));
            var defaultProfile = Path.GetFullPath(Path.Combine(DefaultChromeUserDataDir, "Default"));
            if (string.Equals(full, defaultProfile, StringComparison.OrdinalIgnoreCase))
                return "";

            var parent = Path.GetFileName(Path.GetDirectoryName(full));
            var profile = Path.GetFileName(full);
            if (string.IsNullOrEmpty(parent)) return Sanitize(profile);
            var combined = parent + "." + profile;
            return Sanitize(combined);
        }

        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.')
                    sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// Build the expected AUMID = base + "." + profileId (or just base when profileId is empty).
        /// Spaces in the final AUMID are replaced with "_" (Chromium rule).
        /// </summary>
        public static string BuildExpectedAumid(string baseAumid, string profileId)
        {
            var aumid = string.IsNullOrEmpty(profileId) ? baseAumid : baseAumid + "." + profileId;
            return aumid.Replace(' ', '_');
        }

        /// <summary>
        /// Stable application-defined identity used by YTray's staged Windows launcher. It is
        /// intentionally independent of Chromium's internal profile AUMID algorithm: the same
        /// instance keeps its taskbar group across restarts, while different instance IDs cannot
        /// collapse into the same Chrome taskbar button.
        /// </summary>
        public static string BuildInstanceAumid(BrowserKind kind, string badge, Guid instanceId)
        {
            var browser = Sanitize(kind.ToString());
            var label = Sanitize(DockBadgeLabel.Normalize(badge));
            return $"YTray.{browser}.Inst{label}.{instanceId:N}";
        }

        /// <summary>
        /// Full robust resolution. Returns the AUMID to persist into instance metadata.
        /// Strategy:
        ///   - Try reading the live window AUMID (most authoritative — Chrome itself set it).
        ///   - Fallback to the Chromium-rule computation from base + profile path.
        ///   - Final fallback to the base AUMID alone.
        /// </summary>
        public static async Task<string> ResolveAsync(int processId, string profilePath, BrowserKind kind, TimeSpan? pollTimeout = null)
        {
            var timeout = pollTimeout ?? TimeSpan.FromSeconds(15);

            // Step 1: base AUMID from shortcut (fast, synchronous).
            var baseAumid = ShellLink.ResolveBaseAumid(kind);

            // Step 3: compute expected value per Chromium profile-ID rule.
            var profileId = ComputeProfileId(profilePath);
            var expected = BuildExpectedAumid(baseAumid, profileId);

            // Step 2: read the real AUMID from the launched Chrome's first window.
            // Run on a background thread to avoid blocking the UI; EnumWindows is blocking.
            string windowAumid = null;
            await Task.Run(() =>
            {
                windowAumid = WindowEnum.PollForWindowAumid(processId, timeout);
            }).ConfigureAwait(false);

            // Prefer the window's real AUMID (Chrome sets it itself). If empty, use the computed value,
            // which aligns with what Chrome would set. Final fallback: base AUMID.
            if (!string.IsNullOrEmpty(windowAumid))
                return windowAumid;
            if (!string.IsNullOrEmpty(expected) && expected != baseAumid)
                return expected;
            return baseAumid;
        }

        /// <summary>Synchronous variant for tests / smoke mode.</summary>
        public static string Resolve(int processId, string profilePath, BrowserKind kind, TimeSpan? pollTimeout = null)
        {
            return ResolveAsync(processId, profilePath, kind, pollTimeout).GetAwaiter().GetResult();
        }
    }
}
