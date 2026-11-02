import type { StateSnapshot } from '@shared/types'
import { InstanceRow } from '../components/InstanceRow'
import { Button, Empty } from '../components/ui'
import { run } from '../lib/api'

export function InstancesPage({ state }: { state: StateSnapshot }) {
  const running = state.instances.filter((instance) => instance.status === 'running')
  const history = state.instances
    .filter((instance) => instance.status !== 'running')
    .sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt))

  return (
    <>
      <div className="page-head">
        <div>
          <h1>运行与历史</h1>
          <p>停止实例会保留同一身份、用户目录与最近页面，可随时恢复。</p>
        </div>
        <div className="actions">
          {history.length > 0 ? (
            <Button size="sm" variant="danger" onClick={() => void run({ type: 'removeAllHistory' })}>
              清空历史
            </Button>
          ) : null}
        </div>
      </div>

      <div className="card">
        <div className="card-title">
          运行中
          <span className="hint">{running.length} 个实例</span>
        </div>
        {running.length === 0 ? (
          <Empty title="没有正在运行的实例" hint="在“快速启动”中选择直连或代理启动。" />
        ) : (
          <div className="list">
            {running.map((instance) => (
              <InstanceRow key={instance.id} instance={instance} />
            ))}
          </div>
        )}
      </div>

      <div className="card">
        <div className="card-title">
          历史
          <span className="hint">{history.length} 条记录</span>
        </div>
        {history.length === 0 ? (
          <Empty title="暂无历史记录" hint="实例停止后会自动进入历史。" />
        ) : (
          <div className="list">
            {history.map((instance) => (
              <InstanceRow key={instance.id} instance={instance} />
            ))}
          </div>
        )}
      </div>
    </>
  )
}
