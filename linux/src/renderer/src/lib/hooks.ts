import { useEffect, useState } from 'react'
import type { StateSnapshot } from '@shared/types'

export function useStore(): StateSnapshot | null {
  const [state, setState] = useState<StateSnapshot | null>(null)

  useEffect(() => {
    let active = true
    void window.ytray.getState().then((value) => {
      if (active) setState(value)
    })
    const unsubscribe = window.ytray.onState((value) => setState(value))
    return () => {
      active = false
      unsubscribe()
    }
  }, [])

  return state
}

export function useSystemDark(): boolean {
  const [dark, setDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const handler = (event: MediaQueryListEvent): void => setDark(event.matches)
    media.addEventListener('change', handler)
    return () => media.removeEventListener('change', handler)
  }, [])
  return dark
}

export function useTheme(preference: 'system' | 'light' | 'dark' | undefined): void {
  const systemDark = useSystemDark()
  useEffect(() => {
    const dark = preference === 'dark' || (preference !== 'light' && systemDark)
    document.documentElement.dataset.theme = dark ? 'dark' : 'light'
  }, [preference, systemDark])
}
