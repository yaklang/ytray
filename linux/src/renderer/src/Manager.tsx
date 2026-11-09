import { useState } from 'react'
import type { StateSnapshot } from '@shared/types'
import iconUrl from './assets/icon.png'
import { QuickPage } from './pages/QuickPage'
import { CustomPage } from './pages/CustomPage'
import { InstancesPage } from './pages/InstancesPage'
import { RuntimePage } from './pages/RuntimePage'
import { PluginsPage } from './pages/PluginsPage'
import { McpPage } from './pages/McpPage'
import { SettingsPage } from './pages/SettingsPage'

type Section = 'quick' | 'custom' | 'instances' | 'runtimes' | 'plugins' | 'mcp' | 'settings'

const sections: Array<{ id: Section; label: string }> = [
  { id: 'quick', label: '快速启动' },
  { id: 'custom', label: '自定义启动' },
  { id: 'instances', label: '运行与历史' },
  { id: 'runtimes', label: '浏览器' },
  { id: 'plugins', label: '插件' },
  { id: 'mcp', label: 'MCP' },
  { id: 'settings', label: '设置' }
]

export function Manager({ state }: { state: StateSnapshot }) {
  const [section, setSection] = useState<Section>('quick')
  const running = state.instances.filter((instance) => instance.status === 'running').length

  return (
    <div className="manager">
      <aside className="sidebar">
        <div className="brand">
          <img src={iconUrl} alt="" />
          <div>
            <strong>YTray</strong>
            <span>Linux · {state.platform.version}</span>
          </div>
        </div>

        {sections.map((item) => (
          <button
            key={item.id}
            className={`nav-item ${section === item.id ? 'active' : ''}`}
            onClick={() => setSection(item.id)}
          >
            {item.label}
            {item.id === 'instances' && running > 0 ? <span className="count">{running}</span> : null}
            {item.id === 'runtimes' ? <span className="count">{state.runtimes.length}</span> : null}
            {item.id === 'plugins' ? <span className="count">{state.plugins.length}</span> : null}
          </button>
        ))}

        <div className="sidebar-footer">
          <div>{state.platform.sessionType === 'wayland' ? 'Wayland 会话' : state.platform.sessionType === 'x11' ? 'X11 会话' : '未知会话'}</div>
          <div style={{ marginTop: 4 }}>{state.mcp.available ? 'MCP 已启动' : 'MCP 未启动'}</div>
        </div>
      </aside>

      <main className="content">
        {section === 'quick' ? <QuickPage state={state} /> : null}
        {section === 'custom' ? <CustomPage state={state} /> : null}
        {section === 'instances' ? <InstancesPage state={state} /> : null}
        {section === 'runtimes' ? <RuntimePage state={state} /> : null}
        {section === 'plugins' ? <PluginsPage state={state} /> : null}
        {section === 'mcp' ? <McpPage state={state} /> : null}
        {section === 'settings' ? <SettingsPage state={state} /> : null}
      </main>
    </div>
  )
}
