import { useEffect, useState, type ButtonHTMLAttributes, type ReactNode } from 'react'
import { subscribeNotifications } from '../lib/api'
import { badgeColor, contrastingText } from '../lib/format'

export function Button({
  variant = 'default',
  size = 'md',
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'default' | 'primary' | 'ghost' | 'danger'; size?: 'md' | 'sm' }) {
  return (
    <button className={`btn ${variant} ${size === 'sm' ? 'sm' : ''}`} {...rest}>
      {children}
    </button>
  )
}

export function Switch({
  checked,
  onChange,
  label
}: {
  checked: boolean
  onChange: (value: boolean) => void
  label?: string
}) {
  return (
    <button
      type="button"
      className={`switch ${checked ? 'on' : ''}`}
      aria-pressed={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
    />
  )
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <div className="field">
      <label>{label}</label>
      {children}
      {hint ? <span className="small muted">{hint}</span> : null}
    </div>
  )
}

export function Badge({ label }: { label: string | undefined }) {
  if (!label) return null
  return (
    <span className="badge" style={{ background: badgeColor(label), color: contrastingText(label) }}>
      {label}
    </span>
  )
}

export function Pill({ children, tone = 'default' }: { children: ReactNode; tone?: 'default' | 'ok' | 'warn' | 'err' }) {
  return <span className={`pill ${tone === 'default' ? '' : tone}`}>{children}</span>
}

export function Progress({ percent }: { percent: number }) {
  return (
    <div className="progress">
      <span style={{ width: `${Math.max(0, Math.min(100, percent))}%` }} />
    </div>
  )
}

export function Empty({ title, hint, action }: { title: string; hint?: string; action?: ReactNode }) {
  return (
    <div className="empty">
      <strong>{title}</strong>
      {hint ? <span className="small">{hint}</span> : null}
      {action}
    </div>
  )
}

export function Toaster() {
  const [toast, setToast] = useState<{ message: string; error: boolean; id: number } | null>(null)

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    return subscribeNotifications((message, error) => {
      setToast({ message, error, id: Date.now() })
      if (timer) clearTimeout(timer)
      timer = setTimeout(() => setToast(null), 3200)
    })
  }, [])

  if (!toast) return null
  return (
    <div key={toast.id} className={`toast ${toast.error ? 'error' : ''}`}>
      {toast.message}
    </div>
  )
}
