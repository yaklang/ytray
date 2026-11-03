import type { CommandResult, RendererCommand } from '@shared/types'

const listeners = new Set<(message: string, error: boolean) => void>()

export function notify(message: string, error = false): void {
  for (const listener of listeners) listener(message, error)
}

export function subscribeNotifications(listener: (message: string, error: boolean) => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** Runs a renderer command and surfaces failures through the toast channel. */
export async function run(command: RendererCommand): Promise<CommandResult> {
  const result = await window.ytray.command(command)
  if (!result.ok) notify(result.error ?? '操作失败', true)
  else if (result.message) notify(result.message)
  return result
}
