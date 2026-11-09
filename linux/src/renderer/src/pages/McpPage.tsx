import type { StateSnapshot } from '@shared/types'
import { Button, Pill, Switch } from '../components/ui'
import { run } from '../lib/api'

const tools = [
  ['list_browser_runtimes', '列出可用浏览器及默认运行时'],
  ['list_browser_instances', '列出运行中实例，可选包含历史'],
  ['open_browser_instance', '打开新实例或恢复历史实例'],
  ['focus_browser_instance', '把运行中实例切到前台'],
  ['stop_browser_instance', '停止实例并保留历史']
]

export function McpPage({ state }: { state: StateSnapshot }) {
  return (
    <>
      <div className="page-head">
        <div>
          <h1>MCP</h1>
          <p>Streamable HTTP 服务只监听回环地址，并校验 Host 与 Origin，便于本机 agent 控制浏览器实例。</p>
        </div>
        <div className="actions">
          <Pill tone={state.mcp.available ? 'ok' : 'default'}>{state.mcp.available ? '运行中' : '未运行'}</Pill>
        </div>
      </div>

      <div className="card">
        <div className="card-title">服务</div>
        <div className="stack">
          <div className="row">
            <span className="mono">{state.mcp.endpoint}</span>
            <div style={{ marginLeft: 'auto' }}>
              <Button
                size="sm"
                onClick={() => void navigator.clipboard.writeText(state.mcp.endpoint)}
              >
                复制地址
              </Button>
            </div>
          </div>
          <div className="row">
            <Button size="sm" variant="primary" disabled={state.mcp.available} onClick={() => void run({ type: 'startMcp' })}>
              启动
            </Button>
            <Button size="sm" disabled={!state.mcp.available} onClick={() => void run({ type: 'stopMcp' })}>
              停止
            </Button>
            <span className="small muted">{state.mcp.message}</span>
          </div>
          <div className="row">
            <span>随应用启动</span>
            <div style={{ marginLeft: 'auto' }}>
              <Switch
                checked={state.mcp.autoStart}
                label="随应用启动 MCP"
                onChange={(value) => void run({ type: 'setSettings', patch: { mcpAutoStart: value } })}
              />
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-title">工具</div>
        <div className="stack">
          {tools.map(([name, description]) => (
            <div key={name} className="row">
              <span className="mono">{name}</span>
              <span className="small muted" style={{ marginLeft: 'auto' }}>{description}</span>
            </div>
          ))}
        </div>
      </div>
    </>
  )
}
