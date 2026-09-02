#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YTray.Core
{
    internal static class PluginDiscovery
    {
        private const int MaximumDepth = 5;
        private const int MaximumPlugins = 256;
        private const int MaximumDirectories = 4096;

        public static IReadOnlyList<string> FindDirectories(IEnumerable<string> roots)
        {
            var found = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string Path, int Depth)>();
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                try { pending.Enqueue((Path.GetFullPath(root), 0)); } catch { }
            }

            while (pending.Count > 0 && found.Count < MaximumPlugins && visited.Count < MaximumDirectories)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current.Path) || !Directory.Exists(current.Path)) continue;
                if (File.Exists(Path.Combine(current.Path, "manifest.json")))
                {
                    found.Add(current.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    continue;
                }
                if (current.Depth >= MaximumDepth) continue;

                string[] children;
                try { children = Directory.GetDirectories(current.Path); }
                catch { continue; }
                foreach (var child in children)
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Enqueue((child, current.Depth + 1));
                    }
                    catch { }
                }
            }

            // ponytail: 4,096 directories / 256 extensions / five levels covers browser profile roots; raise only
            // if a real profile exceeds this guard against accidentally scanning an entire disk.
            return found;
        }
    }
}
