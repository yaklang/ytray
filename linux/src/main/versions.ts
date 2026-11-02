/** Dotted numeric version comparison; parse failures fall back to ordinal order. */
export function compareVersions(left: string, right: string): number {
  const l = left.trim().replace(/^[vV]/, '')
  const r = right.trim().replace(/^[vV]/, '')
  const leftParts = l.split('.')
  const rightParts = r.split('.')
  const count = Math.max(leftParts.length, rightParts.length)
  for (let index = 0; index < count; index += 1) {
    const lp = leftParts[index] ?? ''
    const rp = rightParts[index] ?? ''
    const ln = Number.parseInt(lp, 10)
    const rn = Number.parseInt(rp, 10)
    if (Number.isFinite(ln) && Number.isFinite(rn) && /^\d+$/.test(lp) && /^\d+$/.test(rp)) {
      if (ln !== rn) return ln < rn ? -1 : 1
    } else if (lp !== rp) {
      return lp < rp ? -1 : 1
    }
  }
  return l === r ? 0 : l < r ? -1 : 1
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}
