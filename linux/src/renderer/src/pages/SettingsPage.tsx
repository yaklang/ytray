import type { StateSnapshot } from '@shared/types'
import { Button, Field, Pill, Progress, Switch } from '../components/ui'
import { run } from '../lib/api'

export function SettingsPage({ state }: { state: StateSnapshot }) {
  const update = state.update

  return (
    <>
      <div className="page-head">
        <div>
          <h1>设置</h1>
          <p>外观、开机启动与应用更新。后台只检查和提示，安装由你手动确认。</p>
        </div>
      </div>

      <div className="card">
        <div className="card-title">外观</div>
        <div className="stack">
          <Field label="主题">
            <select
              value={state.settings.themePreference}
              onChange={(event) =>
                void run({
                  type: 'setSettings',
                  patch: { themePreference: event.target.value as 'system' | 'light' | 'dark' }
                })
              }
            >
              <option value="system">跟随系统</option>
              <option value="dark">深色</option>
              <option value="light">浅色</option>
            </select>
          </Field>
        </div>
      </div>

      <div className="card">
        <div className="card-title">开机启动</div>
        <div className="row">
          <span>登录桌面后自动启动 YTray（只进托盘，不打开浏览器）</span>
          <div style={{ marginLeft: 'auto' }}>
            <Switch
              checked={state.settings.launchAtLogin}
              label="开机启动"
              onChange={(value) => void run({ type: 'setSettings', patch: { launchAtLogin: value } })}
            />
          </div>
        </div>
        <p className="small muted" style={{ marginBottom: 0 }}>
          通过 XDG autostart 写入 ~/.config/autostart/ytray.desktop。
          {state.platform.isPackaged ? '' : ' 源码开发构建不会修改开机启动项。'}
        </p>
      </div>

      <div className="card">
        <div className="card-title">
          应用更新
          <span className="hint">当前版本 {update.currentVersion}</span>
        </div>
        <div className="stack">
          <div className="row">
            <Button size="sm" onClick={() => void run({ type: 'checkForUpdates' })} disabled={update.phase === 'checking' || update.phase === 'downloading'}>
              检查更新
            </Button>
            {update.phase === 'available' ? (
              <Button size="sm" variant="primary" onClick={() => void run({ type: 'installUpdate' })}>
                下载并安装 {update.availableVersion}
              </Button>
            ) : null}
            <Pill
              tone={
                update.phase === 'failed'
                  ? 'err'
                  : update.phase === 'available' || update.phase === 'downloading'
                    ? 'warn'
                    : update.phase === 'upToDate'
                      ? 'ok'
                      : 'default'
              }
            >
              {update.message || '尚未检查'}
            </Pill>
          </div>
          {update.phase === 'downloading' || update.phase === 'installing' ? <Progress percent={update.progressPercent} /> : null}
          {update.releaseNotes ? <pre className="small muted" style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{update.releaseNotes}</pre> : null}
          {!state.platform.appImagePath ? (
            <p className="small muted" style={{ margin: 0 }}>
              当前不是 AppImage 运行，应用内更新不可用；请从 Releases 下载新的 AppImage。
            </p>
          ) : null}
        </div>
      </div>

      <div className="card">
        <div className="card-title">运行环境</div>
        <div className="stack small">
          <div className="row">
            <span className="muted">数据目录</span>
            <span className="mono" style={{ marginLeft: 'auto' }}>{state.platform.dataDirectory}</span>
          </div>
          <div className="row">
            <span className="muted">会话类型</span>
            <span style={{ marginLeft: 'auto' }}>{state.platform.sessionType}</span>
          </div>
          <div className="row">
            <span className="muted">版本</span>
            <span style={{ marginLeft: 'auto' }}>{state.platform.version}</span>
          </div>
          {state.platform.notes.map((note) => (
            <div key={note} className="muted">{note}</div>
          ))}
        </div>
      </div>

      <div className="card">
        <div className="card-title">退出</div>
        <Button variant="danger" size="sm" onClick={() => void run({ type: 'quit' })}>
          退出 YTray
        </Button>
      </div>
    </>
  )
}
