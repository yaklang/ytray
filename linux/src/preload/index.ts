import { contextBridge, ipcRenderer } from 'electron'
import type { CommandResult, RendererCommand, StateSnapshot } from '../shared/types'

const api = {
  getState: (): Promise<StateSnapshot> => ipcRenderer.invoke('ytray:get-state') as Promise<StateSnapshot>,
  command: (command: RendererCommand): Promise<CommandResult> =>
    ipcRenderer.invoke('ytray:command', command) as Promise<CommandResult>,
  pickBrowserFile: (): Promise<string | null> => ipcRenderer.invoke('ytray:pick-file') as Promise<string | null>,
  pickPluginDirectories: (): Promise<string[]> => ipcRenderer.invoke('ytray:pick-directory') as Promise<string[]>,
  onState: (listener: (state: StateSnapshot) => void): (() => void) => {
    const handler = (_event: unknown, state: StateSnapshot): void => listener(state)
    ipcRenderer.on('ytray:state', handler)
    return () => {
      ipcRenderer.off('ytray:state', handler)
    }
  },
  reportWidgetHeight: (height: number): void => {
    ipcRenderer.send('ytray:widget-height', height)
  }
}

contextBridge.exposeInMainWorld('ytray', api)

export type YtrayApi = typeof api
