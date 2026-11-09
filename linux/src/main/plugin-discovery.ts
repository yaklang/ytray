import fs from 'node:fs'
import path from 'node:path'

const MAX_DEPTH = 5
const MAX_PLUGINS = 256
const MAX_DIRECTORIES = 4096

/** Breadth-first search for unpacked extension roots (directories holding manifest.json). */
export function findPluginDirectories(roots: string[]): string[] {
  const found: string[] = []
  const visited = new Set<string>()
  const queue: Array<{ directory: string; depth: number }> = []

  for (const root of roots) {
    if (!root.trim()) continue
    try {
      queue.push({ directory: fs.realpathSync(root), depth: 0 })
    } catch {
      /* missing roots are reported by the caller */
    }
  }

  while (queue.length > 0 && found.length < MAX_PLUGINS && visited.size < MAX_DIRECTORIES) {
    const current = queue.shift()
    if (!current || visited.has(current.directory)) continue
    visited.add(current.directory)

    let stat: fs.Stats
    try {
      stat = fs.statSync(current.directory)
    } catch {
      continue
    }
    if (!stat.isDirectory()) continue

    if (fs.existsSync(path.join(current.directory, 'manifest.json'))) {
      found.push(current.directory)
      continue
    }
    if (current.depth >= MAX_DEPTH) continue

    try {
      for (const entry of fs.readdirSync(current.directory, { withFileTypes: true })) {
        if (!entry.isDirectory() || entry.isSymbolicLink()) continue
        queue.push({ directory: path.join(current.directory, entry.name), depth: current.depth + 1 })
      }
    } catch {
      /* unreadable directories are skipped */
    }
  }
  return found
}
