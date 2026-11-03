import type { StateSnapshot } from '@shared/types'
import { Button, Empty, Pill, Progress, Switch } from '../components/ui'
import { basename, shortPath } from '../lib/format'
import { run } from '../lib/api'

export function PluginsPage({ state }: { state: StateSnapshot }) {
  const managedName = 'Yakit Browser Agent'
  const managed = state.plugins.find((plugin) => plugin.name === managedName)

  return (
    <>
      <div className="page-head">
        <div>
          <h1>插件</h1>
          <p>
            本地插件需要已解压目录。Branded Chrome 137+ 会通过 CDP 加载并在启动后回读确认，
            失败时不会悄悄跳过。
          </p>
        </div>
        <div className="actions">
          <Button
            size="sm"
            onClick={async () => {
              const paths = await window.ytray.pickPluginDirectories()
              if (paths.length > 0) await run({ type: 'addPlugins', paths })
            }}
          >
            添加本地插件
          </Button>
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          内置 Yakit 插件
          <span className="hint">{managed ? `已安装 ${managed.version}` : '未安装'}</span>
        </div>
        <div className="stack">
          <div className="row">
            <span>版本状态</span>
            <div style={{ marginLeft: 'auto' }}>
              {state.extension.updateAvailable ? <Pill tone="warn">有可用更新</Pill> : <Pill tone="ok">已是最新</Pill>}
            </div>
          </div>
          <div className="row">
            <Button size="sm" onClick={() => void run({ type: 'refreshExtensionManifest' })}>
              刷新插件清单
            </Button>
            <Button
              size="sm"
              variant="primary"
              disabled={state.extension.installing}
              onClick={() => void run({ type: 'installExtension' })}
            >
              安装 / 修复
            </Button>
            {state.extension.message ? <span className="small muted">{state.extension.message}</span> : null}
          </div>
          {state.extension.installing ? <Progress percent={state.extension.percent} /> : null}
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          已加载插件
          <span className="hint">{state.plugins.length} 个</span>
        </div>
        {state.plugins.length === 0 ? (
          <Empty title="还没有插件" hint="添加已解压的 Chrome 扩展目录。" />
        ) : (
          <div className="list">
            {state.plugins.map((plugin) => (
              <div key={plugin.id} className="instance" style={{ gridTemplateColumns: '1fr auto' }}>
                <div>
                  <div className="title">
                    {plugin.name}
                    <Pill>v{plugin.version || '未知'}</Pill>
                    {plugin.name === managedName ? <Pill tone="ok">内置</Pill> : null}
                  </div>
                  <div className="meta">
                    <span>Manifest V{plugin.manifestVersion}</span>
                    <span className="mono" title={plugin.path}>{shortPath(plugin.path)}</span>
                  </div>
                </div>
                <div className="actions">
                  <label className="row small" style={{ gap: 6 }}>
                    启用
                    <Switch
                      checked={plugin.enabled}
                      label={`启用 ${basename(plugin.path)}`}
                      onChange={(value) => void run({ type: 'updatePlugin', pluginId: plugin.id, patch: { enabled: value } })}
                    />
                  </label>
                  <label className="row small" style={{ gap: 6 }}>
                    固定工具栏
                    <Switch
                      checked={plugin.pinToToolbar === true}
                      label={`固定 ${plugin.name}`}
                      onChange={(value) =>
                        void run({ type: 'updatePlugin', pluginId: plugin.id, patch: { pinToToolbar: value } })
                      }
                    />
                  </label>
                  {plugin.name !== managedName ? (
                    <Button size="sm" variant="ghost" onClick={() => void run({ type: 'removePlugin', pluginId: plugin.id })}>
                      移除
                    </Button>
                  ) : null}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </>
  )
}
