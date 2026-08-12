#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using YTray.Models;

namespace YTray.Native
{
    /// <summary>
    /// Reads the AppUserModelID from a Chrome .lnk shortcut, and writes shortcuts
    /// carrying a custom AUMID + icon for per-instance taskbar grouping.
    /// </summary>
    public static class ShellLink
    {
        /// <summary>
        /// Read System.AppUserModel.ID from a .lnk file.
        /// </summary>
        public static string? ReadAumidFromLnk(string lnkPath)
        {
            if (!File.Exists(lnkPath)) return null;
            ShellLinkClass? shellLink = null;
            try
            {
                shellLink = new ShellLinkClass();
                var pf = (IPersistFile)shellLink;
                if (pf.Load(lnkPath, 0) != 0) return null; // STGM_READ
                var ps = shellLink as IPropertyStore;
                if (ps == null) return null;
                var pv = new PROPVARIANT();
                var key = Win32.PKEY_AppUserModel_ID;
                try
                {
                    ps.GetValue(ref key, pv);
                    return pv.AsString();
                }
                finally
                {
                    pv.Dispose();
                }
            }
            catch
            {
                return null;
            }
            finally { ReleaseShellLink(shellLink); }
        }

        /// <summary>Read target path + arguments from a .lnk file.</summary>
        public static (string? target, string? arguments) ReadTargetAndArgs(string lnkPath)
        {
            ShellLinkClass? shellLink = null;
            try
            {
                shellLink = new ShellLinkClass();
                var pf = (IPersistFile)shellLink;
                if (pf.Load(lnkPath, 0) != 0) return (null, null);
                var islw = (IShellLinkW)shellLink;
                var target = new System.Text.StringBuilder(260);
                var args = new System.Text.StringBuilder(1024);
                islw.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                islw.GetArguments(args, args.Capacity);
                return (target.ToString(), args.ToString());
            }
            catch
            {
                return (null, null);
            }
            finally { ReleaseShellLink(shellLink); }
        }

        /// <summary>
        /// Write a .lnk with a custom AUMID and icon resource path.
        /// This makes the Chrome instance group under its own taskbar button with the composited icon.
        /// </summary>
        public static void WriteLnk(string lnkPath, string target, string arguments,
            string workingDir, string iconPath, int iconIndex, string aumid, string displayName)
        {
            if (string.IsNullOrWhiteSpace(lnkPath)) throw new ArgumentException("Shortcut path is required.", nameof(lnkPath));
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Shortcut target is required.", nameof(target));
            ShellLinkClass? shellLink = null;
            try
            {
                shellLink = new ShellLinkClass();
                var islw = (IShellLinkW)shellLink;
                islw.SetPath(target);
                if (!string.IsNullOrEmpty(arguments)) islw.SetArguments(arguments);
                if (!string.IsNullOrEmpty(workingDir)) islw.SetWorkingDirectory(workingDir);
                if (!string.IsNullOrEmpty(iconPath)) islw.SetIconLocation(iconPath, iconIndex);
                if (!string.IsNullOrEmpty(displayName)) islw.SetDescription(displayName);

                // Set AUMID via the shortcut's property store (must precede/accompany the ID).
                var ps = shellLink as IPropertyStore;
                if (ps != null && !string.IsNullOrEmpty(aumid))
                {
                    var pv = PROPVARIANT.FromString(aumid);
                    var key = Win32.PKEY_AppUserModel_ID;
                    try
                    {
                        ps.SetValue(ref key, pv);
                        ps.Commit();
                    }
                    finally { pv.Dispose(); }
                }

                var directory = Path.GetDirectoryName(lnkPath);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new ArgumentException("Shortcut path must include a directory.", nameof(lnkPath));
                Directory.CreateDirectory(directory);
                ((IPersistFile)shellLink).Save(lnkPath, true);
            }
            finally { ReleaseShellLink(shellLink); }
        }

        private static void ReleaseShellLink(ShellLinkClass? shellLink)
        {
            if (shellLink == null || !Marshal.IsComObject(shellLink)) return;
            try { Marshal.FinalReleaseComObject(shellLink); } catch { }
        }

        /// <summary>
        /// Best-effort: locate the installed Chrome .lnk shortcut to read the base AUMID.
        /// Falls back to a per-kind default if no shortcut is found.
        /// </summary>
        public static string ResolveBaseAumid(BrowserKind kind)
        {
            var candidates = DiscoverShortcutCandidates(kind);
            foreach (var path in candidates)
            {
                var aumid = ReadAumidFromLnk(path);
                if (!string.IsNullOrEmpty(aumid)) return aumid!;
            }
            // Fallback: the canonical base AUMID per browser kind.
            return kind switch
            {
                BrowserKind.Chrome => "Chrome",
                BrowserKind.ChromeBeta => "Chrome.Beta",
                BrowserKind.ChromeCanary => "Chrome.Canary",
                BrowserKind.ChromeForTesting => "Chrome",
                BrowserKind.Chromium => "Chromium",
                BrowserKind.Edge => "MicrosoftEdge",
                _ => "Chrome",
            };
        }

        private static IEnumerable<string> DiscoverShortcutCandidates(BrowserKind kind)
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            var userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var taskbar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

            var names = new List<string>();
            switch (kind)
            {
                case BrowserKind.Chrome:
                    names.AddRange(new[] { "Google Chrome.lnk", "Chrome.lnk" });
                    break;
                case BrowserKind.ChromeBeta:
                    names.Add("Google Chrome Beta.lnk");
                    break;
                case BrowserKind.ChromeCanary:
                    names.Add("Google Chrome Canary.lnk");
                    break;
                case BrowserKind.Edge:
                    names.AddRange(new[] { "Microsoft Edge.lnk", "Edge.lnk" });
                    break;
                case BrowserKind.Chromium:
                    names.Add("Chromium.lnk");
                    break;
            }

            var roots = new[] { programData, userPrograms, desktop, userDesktop, taskbar };
            foreach (var root in roots)
            foreach (var name in names)
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) yield return path;
                // recursive shallow search (Start Menu subfolders)
                if (Directory.Exists(root))
                {
                    List<string>? found = null;
                    try
                    {
                        found = new List<string>(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories));
                    }
                    catch { }
                    if (found != null)
                        foreach (var f in found)
                            if (File.Exists(f)) yield return f;
                }
            }
        }
    }
}
