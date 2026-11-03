import { useState } from 'react'
import type { StateSnapshot } from '@shared/types'
import { Button, Empty, Pill, Progress } from '../components/ui'
import { shortPath } from '../lib/format'
import { run } from '../lib/api'

export function RuntimePage({ state }: { state: StateSnapshot }) {
  const [selectedPath, setSelectedPath] = useState('')
  const defaultId = state.settings.defaultRuntimeId

  return (
    <>
      <div className="page-head">
        <div>
          <h1>浏览器运行时</h1>
          <p>扫描本机 Chromium 浏览器，或安装由 YTray 托管的 Chrome for Testing。</p>
        </div>
        <div className="actions">
          <Button size="sm" onClick={() => void run({ type: 'refreshBrowsers' })}>
            重新扫描
          </Button>
        </div>
      </div>

      {state.errorMessage ? <div className="banner error">{state.errorMessage}</div> : null}

      <div className="card">
        <div className="card-title">
          已发现
          <span className="hint">{state.runtimes.length} 个运行时</span>
        </div>
        {state.runtimes.length === 0 ? (
          <Empty
            title="没有发现浏览器"
            hint="请安装 Chromium/Chrome/Edge，或从下方安装 Chrome for Testing。"
          />
        ) : (
          <div className="list">
            {state.runtimes.map((runtime) => (
              <div key={runtime.id} className="instance" style={{ gridTemplateColumns: '1fr auto' }}>
                <div>
                  <div className="title">
                    {runtime.name}
                    {runtime.id === defaultId ? <Pill tone="ok">默认</Pill> : null}
                    <Pill>{runtime.source === 'managed' ? 'YTray 安装' : runtime.source === 'system' ? '系统环境' : '自定义'}</Pill>
                  </div>
                  <div className="meta">
                    <span>{runtime.version}</span>
                    <span>{runtime.architecture}</span>
                    <span className="mono" title={runtime.executablePath}>{shortPath(runtime.executablePath)}</span>
                  </div>
                </div>
                <div className="actions">
                  {runtime.id !== defaultId ? (
                    <Button size="sm" onClick={() => void run({ type: 'selectDefaultRuntime', runtimeId: runtime.id })}>
                      设为默认
                    </Button>
                  ) : null}
                  {runtime.source === 'managed' ? (
                    <Button size="sm" variant="danger" onClick={() => void run({ type: 'uninstallRuntime', runtimeId: runtime.id })}>
                      卸载
                    </Button>
                  ) : (
                    <Button size="sm" variant="ghost" onClick={() => void run({ type: 'removeRuntime', runtimeId: runtime.id })}>
                      移除
                    </Button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="card">
        <div className="card-title">添加本机浏览器</div>
        <div className="row">
          <input
            type="text"
            value={selectedPath}
            placeholder="/usr/bin/google-chrome"
            onChange={(event) => setSelectedPath(event.target.value)}
            style={{ flex: 1, padding: '8px 10px', borderRadius: 8, border: '1px solid var(--border-strong)', background: 'var(--panel-raised)' }}
          />
          <Button
            size="sm"
            onClick={async () => {
              const picked = await window.ytray.pickBrowserFile()
              if (picked) setSelectedPath(picked)
            }}
          >
            选择文件
          </Button>
          <Button
            size="sm"
            variant="primary"
            disabled={!selectedPath.trim()}
            onClick={async () => {
              const result = await run({ type: 'addLocalRuntime', path: selectedPath.trim() })
              if (result.ok) setSelectedPath('')
            }}
          >
            添加
          </Button>
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          Chrome for Testing
          <span className="hint">{state.availableVersions.length} 个可用版本</span>
        </div>
        <div className="row" style={{ marginBottom: 12 }}>
          <Button size="sm" onClick={() => void run({ type: 'refreshRuntimeVersions' })}>
            刷新版本清单
          </Button>
          {state.installation.installing ? (
            <span className="small muted">{state.installation.message}</span>
          ) : state.availableVersions.length === 0 ? (
            <span className="small muted">从 aliyun-oss.yaklang.com 获取官方版本</span>
          ) : null}
        </div>
        {state.installation.installing ? <Progress percent={state.installation.percent} /> : null}
        {state.availableVersions.length === 0 ? (
          <Empty title="尚未获取版本清单" hint="点击“刷新版本清单”后选择版本安装。" />
        ) : (
          <div className="list">
            {state.availableVersions.slice(0, 12).map((version) => {
              const installed = state.runtimes.some(
                (runtime) => runtime.source === 'managed' && runtime.version === version.version
              )
              return (
                <div key={version.version} className="row" style={{ padding: '6px 2px' }}>
                  <span className="mono">{version.version}</span>
                  <span className="small muted">
                    {version.artifacts
                      .filter((artifact) => artifact.os === 'linux')
                      .map((artifact) => `${artifact.arch}/${artifact.format}`)
                      .join(' · ') || '无 Linux 构建'}
                  </span>
                  <div style={{ marginLeft: 'auto' }}>
                    {installed ? (
                      <Pill tone="ok">已安装</Pill>
                    ) : (
                      <Button
                        size="sm"
                        onClick={() => void run({ type: 'installRuntime', version: version.version })}
                        disabled={state.installation?.installing}
                      >
                        安装
                      </Button>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>
    </>
  )
}
