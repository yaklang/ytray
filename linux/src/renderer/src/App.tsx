import { useStore, useTheme } from './lib/hooks'
import { Manager } from './Manager'
import { Widget } from './Widget'
import { Toaster } from './components/ui'

function useView(): 'manager' | 'widget' {
  return window.location.hash.includes('/widget') ? 'widget' : 'manager'
}

export default function App() {
  const state = useStore()
  const view = useView()
  useTheme(state?.settings.themePreference)

  if (!state) {
    return <div className="content muted">正在加载 YTray…</div>
  }

  return (
    <>
      {view === 'widget' ? <Widget state={state} /> : <Manager state={state} />}
      <Toaster />
    </>
  )
}
