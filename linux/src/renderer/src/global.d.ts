import type { CommandResult, RendererCommand, StateSnapshot } from '../../shared/types'

export interface YtrayApi {
  getState(): Promise<StateSnapshot>
  command(command: RendererCommand): Promise<CommandResult>
  pickBrowserFile(): Promise<string | null>
  pickPluginDirectories(): Promise<string[]>
  onState(listener: (state: StateSnapshot) => void): () => void
  reportWidgetHeight(height: number): void
}

declare global {
  interface Window {
    ytray: YtrayApi
  }
}

export {}
