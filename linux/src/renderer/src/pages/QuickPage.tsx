import { useEffect, useState } from 'react'
import type { LaunchSettings, StateSnapshot } from '@shared/types'
import { Button, Field, Pill, Progress, Switch } from '../components/ui'
import { run } from '../lib/api'

export function useDraft(state: StateSnapshot): [LaunchSettings, (patch: Partial<LaunchSettings>, commit?: boolean) => void] {
  const [draft, setDraft] = useState<LaunchSettings>(state.settings)
  useEffect(() => {
    setDraft(state.settings)
  }, [state.settings])

  const update = (patch: Partial<LaunchSettings>, commit = false): void => {
    setDraft((current) => ({ ...current, ...patch }))
    if (commit) void run({ type: 'setSettings', patch })
  }
  return [draft, update]
}

export function QuickPage({ state }: { state: StateSnapshot }) {
  const [draft, update] = useDraft(state)
  const running = state.instances.filter((instance) => instance.status === 'running').length
  const busy = state.launch.phase === 'preparing' || state.launch.phase === 'waiting'

  const checkProxy = async (): Promise<void> => {
    await run({ type: 'setSettings', patch: { presetProxyServer: draft.presetProxyServer } })
    await run({ type: 'checkProxy' })
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>快速启动</h1>
          <p>每个实例使用独立用户目录、调试端口与插件集合。直连启动不会继承系统代理。</p>
        </div>
        <div className="actions">
          <Pill tone={running > 0 ? 'ok' : 'default'}>{running} 个运行中</Pill>
          {state.launch.phase !== 'idle' ? <Pill tone="warn">{state.launch.message}</Pill> : null}
        </div>
      </div>

      {state.errorMessage ? <div className="banner error">{state.errorMessage}</div> : null}

      <div className="card">
        <div className="card-title">
          启动
          <span className="hint">默认浏览器：{state.runtimes.find((item) => item.id === state.settings.defaultRuntimeId)?.name ?? '未选择'}</span>
        </div>
        <div className="row">
          <Button
            variant="primary"
            disabled={busy || state.runtimes.length === 0}
            onClick={() => void run({ type: 'launchQuick', useProxy: false })}
          >
            直连启动
          </Button>
          <Button disabled={busy || state.runtimes.length === 0} onClick={() => void run({ type: 'launchQuick', useProxy: true })}>
            使用预设代理启动
          </Button>
          {busy ? <span className="small muted">{state.launch.message}</span> : null}
        </div>
        <div className="row small muted">
          <span>启动地址</span>
          <input
            type="text"
            value={draft.homeURL}
            onChange={(event) => update({ homeURL: event.target.value })}
            onBlur={() => update({ homeURL: draft.homeURL }, true)}
            style={{ flex: 1, padding: '6px 9px', borderRadius: 8, border: '1px solid var(--border-strong)', background: 'var(--panel-raised)' }}
          />
        </div>
      </div>

      <div className="grid-2">
        <div className="card">
          <div className="card-title">HTTP 代理</div>
          <div className="stack">
            <Field label="代理服务器" hint="留空表示直连（同时绕过系统代理）">
              <input
                type="text"
                value={draft.presetProxyServer}
                placeholder="http://127.0.0.1:8083"
                onChange={(event) => update({ presetProxyServer: event.target.value })}
                onBlur={() => update({ presetProxyServer: draft.presetProxyServer }, true)}
              />
            </Field>
            <div className="row">
              <Field label="用户名">
                <input
                  type="text"
                  value={draft.presetProxyUsername}
                  onChange={(event) => update({ presetProxyUsername: event.target.value })}
                  onBlur={() => update({ presetProxyUsername: draft.presetProxyUsername }, true)}
                />
              </Field>
              <Field label="密码">
                <input
                  type="password"
                  value={draft.presetProxyPassword}
                  onChange={(event) => update({ presetProxyPassword: event.target.value })}
                  onBlur={() => update({ presetProxyPassword: draft.presetProxyPassword }, true)}
                />
              </Field>
            </div>
            <div className="row">
              <Button size="sm" onClick={() => void checkProxy()} disabled={state.proxyCheck.phase === 'checking'}>
                检测连通性
              </Button>
              <Button size="sm" variant="ghost" onClick={() => void run({ type: 'rememberPresetProxy' })}>
                记住当前代理
              </Button>
              <Pill tone={state.proxyCheck.phase === 'success' ? 'ok' : state.proxyCheck.phase === 'failure' ? 'err' : 'default'}>
                {state.proxyCheck.message || '尚未检测'}
              </Pill>
            </div>

            <div className="stack small">
              <div className="row muted">
                <span>最近使用 · {draft.recentProxyPresets.length}/5</span>
              </div>
              {draft.recentProxyPresets.length === 0 ? (
                <span className="muted">保存后会出现在这里，点击即可切换。</span>
              ) : (
                draft.recentProxyPresets.map((preset) => (
                  <div key={preset.id} className="row">
                    <span className="mono" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {preset.server}
                      {preset.username ? ' · 已配置账号' : ''}
                    </span>
                    <Button size="sm" variant="ghost" onClick={() => void run({ type: 'selectProxyPreset', presetId: preset.id })}>
                      使用
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => void run({ type: 'removeProxyPreset', presetId: preset.id })}>
                      删除
                    </Button>
                  </div>
                ))
              )}
            </div>
            {state.proxyCheck.report ? (
              <div className="stack small">
                {state.proxyCheck.report.details.map((detail) => (
                  <div key={detail.target} className="row">
                    <Pill tone={detail.success ? 'ok' : 'err'}>{detail.success ? '成功' : '失败'}</Pill>
                    <span className="mono">{detail.target}</span>
                    <span className="muted" style={{ marginLeft: 'auto' }}>{detail.elapsedMs} ms</span>
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        </div>

        <div className="card">
          <div className="card-title">启动选项</div>
          <div className="stack">
            <Toggle
              label="忽略证书错误"
              checked={draft.ignoreCertificateErrors}
              onChange={(value) => update({ ignoreCertificateErrors: value }, true)}
            />
            <Toggle
              label="禁用通知"
              checked={draft.disableNotifications}
              onChange={(value) => update({ disableNotifications: value }, true)}
            />
            <Toggle
              label="限制 WebRTC"
              checked={draft.restrictWebRTC}
              onChange={(value) => update({ restrictWebRTC: value }, true)}
            />
            <Toggle
              label="测试类型标记"
              checked={draft.useTestType}
              onChange={(value) => update({ useTestType: value }, true)}
            />
            <Toggle
              label="按角标着色窗口"
              checked={draft.colorizeBrowserInstances}
              onChange={(value) => update({ colorizeBrowserInstances: value }, true)}
            />
            <Field label="起始调试端口">
              <input
                type="number"
                value={draft.debugPort}
                min={1024}
                max={65535}
                onChange={(event) => update({ debugPort: Number(event.target.value) })}
                onBlur={() => update({ debugPort: draft.debugPort }, true)}
              />
            </Field>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          自定义启动参数
          <span className="hint">每行一个 --flag；隔离与调试参数不允许覆盖</span>
        </div>
        <textarea
          value={draft.additionalFlags}
          onChange={(event) => update({ additionalFlags: event.target.value })}
          onBlur={() => update({ additionalFlags: draft.additionalFlags }, true)}
          placeholder="--lang=zh-CN"
        />
      </div>

      {state.proxyCheck.phase === 'checking' ? <Progress percent={45} /> : null}
    </>
  )
}

function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="row">
      <span>{label}</span>
      <div style={{ marginLeft: 'auto' }}>
        <Switch checked={checked} onChange={onChange} label={label} />
      </div>
    </div>
  )
}
