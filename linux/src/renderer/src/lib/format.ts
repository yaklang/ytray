const palette = ['#f26215', '#2563eb', '#16a34a', '#7c3aed', '#be185d', '#0f766e']

export function badgeColor(badge: string | undefined): string {
  if (!badge) return palette[0] as string
  let ordinal = 0
  for (const letter of badge.toUpperCase()) {
    const code = letter.charCodeAt(0)
    if (code < 65 || code > 90) return palette[0] as string
    ordinal = ordinal * 26 + code - 64
  }
  return palette[(ordinal - 1) % palette.length] as string
}

export function contrastingText(badge: string | undefined): string {
  const hex = badgeColor(badge)
  const r = Number.parseInt(hex.slice(1, 3), 16)
  const g = Number.parseInt(hex.slice(3, 5), 16)
  const b = Number.parseInt(hex.slice(5, 7), 16)
  const linear = (value: number): number => {
    const channel = value / 255
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4
  }
  const luminance = 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
  return luminance > 0.179 ? '#1b120a' : '#ffffff'
}

export function relativeTime(iso: string | undefined): string {
  if (!iso) return ''
  const time = Date.parse(iso)
  if (!Number.isFinite(time)) return ''
  const diff = Date.now() - time
  if (diff < 60_000) return '刚刚'
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} 分钟前`
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} 小时前`
  return `${Math.floor(diff / 86_400_000)} 天前`
}

export function shortPath(value: string): string {
  const home = value.replace(/^\/home\/[^/]+/, '~')
  return home.length > 64 ? `…${home.slice(-61)}` : home
}

export function basename(value: string): string {
  return value.split('/').filter(Boolean).pop() ?? value
}
