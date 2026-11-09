import { useEffect, useRef } from 'react'
import type { StateSnapshot } from '@shared/types'
import iconUrl from './assets/icon.png'
import { InstanceRow } from './components/InstanceRow'
import { Button, Pill, Progress } from './components/ui'
import { run } from './lib/api'

export function Widget({ state }: { state: StateSnapshot }) {
  const shell = useRef<HTMLDivElement>(null)
  const running = state.instances.filter((instance) => instance.status === 'running')
  const history = state.instances
    .filter((instance) => instance.status !== 'running')
    .sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt))
    .slice(0, 3)
  const busy = state.launch.phase === 'preparing' || state.launch.phase === 'waiting'

  useEffect(() => {
    // A dedicated body class keeps the layout flex container off <body>, which
    // previously collapsed the whole panel to its max-content width.
    document.body.classList.add('widget-mode')
    const element = shell.current
    if (!element) return
    const observer = new ResizeObserver(() => {
      // scrollHeight is the content height; the shell itself is clamped by
      // max-height, so measuring its box would lock the window 27px short and
      // leave the last row behind a permanent scrollbar.
      const height = Math.ceil(element.scrollHeight) + 20
      window.ytray.reportWidgetHeight(height)
    })
    observer.observe(element)
    return () => {
      observer.disconnect()
      document.body.classList.remove('widget-mode')
    }
  }, [])

  return (
    <div className="widget">
      <div className="widget-shell" ref={shell}>
        <header className="widget-head">
          <img src={iconUrl} alt="" />
          <strong>YTray</strong>
          <div className="status">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => void run({ type: 'setWidgetPinned', pinned: !state.widget.pinned })}
              title={state.widget.pinned ? '取消固定后失焦自动隐藏' : '固定后保持显示'}
            >
              {state.widget.pinned ? '已固定' : '固定'}
            </Button>
            <Pill tone={running.length > 0 ? 'ok' : 'default'}>{running.length} 运行</Pill>
          </div>
        </header>

        <div className="proxy-line">
          <span className="small muted">代理</span>
          <span className="server mono">{state.settings.presetProxyServer || '直连'}</span>
          <Button size="sm" variant="ghost" onClick={() => void run({ type: 'checkProxy' })}>
            检测
          </Button>
        </div>

        <div className="launch-row">
          <Button variant="primary" disabled={busy || state.runtimes.length === 0} onClick={() => void run({ type: 'launchQuick', useProxy: false })}>
            直连启动
          </Button>
          <Button disabled={busy || state.runtimes.length === 0} onClick={() => void run({ type: 'launchQuick', useProxy: true })}>
            代理启动
          </Button>
        </div>

        {busy ? (
          <div className="small muted">{state.launch.message}</div>
        ) : state.proxyCheck.message ? (
          <div className="small muted">{state.proxyCheck.message}</div>
        ) : null}

        {state.installation.installing ? <Progress percent={state.installation.percent} /> : null}

        <div className="stack">
          {running.length === 0 ? (
            <div className="small muted">还没有运行中的实例。</div>
          ) : (
            running.map((instance) => <InstanceRow key={instance.id} instance={instance} compact />)
          )}
        </div>

        {history.length > 0 ? (
          <details>
            <summary className="small muted">最近历史 · {history.length}</summary>
            <div className="stack" style={{ marginTop: 8 }}>
              {history.map((instance) => (
                <InstanceRow key={instance.id} instance={instance} compact />
              ))}
            </div>
          </details>
        ) : null}

        {state.errorMessage ? <div className="banner error small">{state.errorMessage}</div> : null}

        <div className="launch-row">
          <Button size="sm" onClick={() => void run({ type: 'openManager' })}>
            管理
          </Button>
          <Button size="sm" onClick={() => void run({ type: 'quit' })}>
            退出
          </Button>
        </div>
      </div>
    </div>
  )
}
