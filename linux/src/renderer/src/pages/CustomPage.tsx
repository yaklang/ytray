import { useState } from 'react'
import type { StateSnapshot } from '@shared/types'
import { Button, Field, Pill, Switch } from '../components/ui'
import { run } from '../lib/api'

export function CustomPage({ state }: { state: StateSnapshot }) {
  const [runtimeId, setRuntimeId] = useState(state.settings.defaultRuntimeId ?? '')
  const [url, setUrl] = useState(state.settings.homeURL)
  const [badge, setBadge] = useState('')
  const [isolated, setIsolated] = useState(false)
  const [useProxy, setUseProxy] = useState(false)
  const [pluginIds, setPluginIds] = useState<string[]>(state.settings.defaultPluginIds)

  const busy = state.launch.phase === 'preparing' || state.launch.phase === 'waiting'

  return (
    <>
      <div className="page-head">
        <div>
          <h1>自定义启动</h1>
          <p>为单次启动覆盖运行时、地址、角标与插件。不会修改全局默认设置。</p>
        </div>
      </div>

      <div className="card">
        <div className="stack">
          <Field label="浏览器运行时">
            <select value={runtimeId} onChange={(event) => setRuntimeId(event.target.value)}>
              {state.runtimes.map((runtime) => (
                <option key={runtime.id} value={runtime.id}>
                  {runtime.name} {runtime.version}
                </option>
              ))}
            </select>
          </Field>
          <Field label="启动地址">
            <input type="text" value={url} onChange={(event) => setUrl(event.target.value)} />
          </Field>
          <div className="row">
            <Field label="实例角标" hint="留空自动分配；1–2 个英文字母">
              <input
                type="text"
                value={badge}
                maxLength={2}
                placeholder="自动"
                onChange={(event) => setBadge(event.target.value.toUpperCase())}
              />
            </Field>
          </div>
          <div className="row">
            <span>隔离模式（不加载插件与代理）</span>
            <div style={{ marginLeft: 'auto' }}>
              <Switch checked={isolated} onChange={setIsolated} label="隔离模式" />
            </div>
          </div>
          <div className="row">
            <span>使用预设代理</span>
            <div style={{ marginLeft: 'auto' }}>
              <Switch checked={useProxy} onChange={setUseProxy} label="使用预设代理" />
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          插件
          <span className="hint">{pluginIds.length} 个已选择</span>
        </div>
        {state.plugins.length === 0 ? (
          <p className="small muted">还没有可用插件。</p>
        ) : (
          <div className="stack">
            {state.plugins.map((plugin) => (
              <div key={plugin.id} className="row">
                <span>{plugin.name}</span>
                <Pill>v{plugin.version || '未知'}</Pill>
                <div style={{ marginLeft: 'auto' }}>
                  <Switch
                    checked={pluginIds.includes(plugin.id)}
                    label={`选择 ${plugin.name}`}
                    onChange={(value) =>
                      setPluginIds((current) =>
                        value ? [...current, plugin.id] : current.filter((id) => id !== plugin.id)
                      )
                    }
                  />
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="row">
        <Button
          variant="primary"
          disabled={busy || !runtimeId || state.runtimes.length === 0}
          onClick={() =>
            void run({
              type: 'launchCustom',
              settings: {
                defaultRuntimeId: runtimeId,
                homeURL: url.trim() || 'chrome://newtab',
                dockBadge: badge.trim(),
                proxyServer: useProxy ? state.settings.presetProxyServer : '',
                proxyUsername: useProxy ? state.settings.presetProxyUsername : '',
                proxyPassword: useProxy ? state.settings.presetProxyPassword : ''
              },
              pluginIds: isolated ? [] : pluginIds,
              isolated
            })
          }
        >
          启动实例
        </Button>
        {isolated ? <span className="small muted">隔离模式使用独立的最小配置</span> : null}
      </div>
    </>
  )
}
