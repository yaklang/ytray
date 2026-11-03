import { useState } from 'react'
import type { BrowserInstance } from '@shared/types'
import { Badge, Button, Pill } from './ui'
import { relativeTime } from '../lib/format'
import { run } from '../lib/api'

function thumbUrl(file: string | undefined): string | null {
  return file ? `ytray-media://local/?path=${encodeURIComponent(file)}` : null
}

export function InstanceRow({ instance, compact = false }: { instance: BrowserInstance; compact?: boolean }) {
  const [renaming, setRenaming] = useState(false)
  const [draft, setDraft] = useState(instance.name)
  const [previewFailed, setPreviewFailed] = useState(false)
  const running = instance.status === 'running'
  const thumb = thumbUrl(instance.thumbnailPath ?? instance.lastScreenshotPath)
  const showThumb = thumb !== null && !previewFailed

  const submitRename = async (): Promise<void> => {
    const name = draft.trim()
    setRenaming(false)
    if (!name || name === instance.name) return
    await run({ type: 'renameHistory', instanceId: instance.id, name })
  }

  return (
    <div className="instance">
      {showThumb ? (
        <img className="thumb" src={thumb} alt="" onError={() => setPreviewFailed(true)} />
      ) : (
        <div className="thumb empty-thumb">无预览</div>
      )}

      <div>
        <div className="title">
          <Badge label={instance.dockBadge} />
          {renaming ? (
            <input
              autoFocus
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              onBlur={() => void submitRename()}
              onKeyDown={(event) => {
                if (event.key === 'Enter') void submitRename()
                if (event.key === 'Escape') setRenaming(false)
              }}
              style={{ flex: 1, padding: '2px 6px', borderRadius: 6, border: '1px solid var(--border-strong)', background: 'var(--panel-raised)' }}
            />
          ) : (
            <span>{instance.name}</span>
          )}
          <Pill tone={running ? 'ok' : instance.status === 'failed' ? 'err' : 'default'}>
            <span className={`dot ${running ? 'running' : ''}`} />
            {running ? '运行中' : instance.status === 'failed' ? '启动失败' : '已停止'}
          </Pill>
        </div>
        <div className="meta">
          <span>{[instance.runtimeName, instance.runtimeVersion].filter(Boolean).join(' ')}</span>
          {running ? <span>PID {instance.processId}</span> : null}
          {running ? <span>调试端口 {instance.debugPort}</span> : null}
          <span>{relativeTime(instance.startedAt)}</span>
          {instance.lastPageTitle ? <span title={instance.lastPageURL}>页面：{instance.lastPageTitle}</span> : null}
        </div>
      </div>

      <div className="actions">
        {running ? (
          <>
            <Button size="sm" onClick={() => void run({ type: 'focusInstance', instanceId: instance.id })}>
              聚焦
            </Button>
            <Button size="sm" onClick={() => void run({ type: 'captureInstance', instanceId: instance.id })}>
              截图
            </Button>
            <Button size="sm" variant="ghost" onClick={() => void run({ type: 'revealProfile', instanceId: instance.id })}>
              目录
            </Button>
            <Button size="sm" variant="danger" onClick={() => void run({ type: 'stopInstance', instanceId: instance.id })}>
              停止
            </Button>
          </>
        ) : (
          <>
            <Button size="sm" variant="primary" onClick={() => void run({ type: 'restoreInstance', instanceId: instance.id })}>
              恢复
            </Button>
            {!compact ? (
              <>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    setDraft(instance.name)
                    setRenaming(true)
                  }}
                >
                  重命名
                </Button>
                <Button size="sm" variant="ghost" onClick={() => void run({ type: 'removeHistory', instanceId: instance.id })}>
                  删除
                </Button>
              </>
            ) : null}
          </>
        )}
      </div>
    </div>
  )
}
