import { browser } from 'wxt/browser';
import type { BridgeLaunchTarget } from '@/types/models';

import { YTRAY_ENGINE_HOST } from './launch-target';

export function nativeEngineOpen(target: BridgeLaunchTarget, purpose: 'pair' | 'connect') {
  return { type: 'yak_engine_open', purpose, pipeName: target.pipeName,
    engineIdentityId: target.engineIdentityId, engineInstanceId: target.engineInstanceId };
}

// The pairing flow uses the same messages over the browser's native port.
export function nativePairingChannel(target: BridgeLaunchTarget) {
  const port = browser.runtime.connectNative(YTRAY_ENGINE_HOST);
  port.postMessage(nativeEngineOpen(target, 'pair'));
  return {
    addEventListener(type: string, callback: (event: { data?: unknown }) => void) {
      if (type === 'open') queueMicrotask(() => callback({}));
      if (type === 'message') port.onMessage.addListener((data) => callback({ data: JSON.stringify(data) }));
      if (type === 'error') port.onDisconnect.addListener(() => {
        if (browser.runtime.lastError) callback({});
      });
      if (type === 'close') port.onDisconnect.addListener(() => callback({}));
    },
    send(message: string) { port.postMessage(JSON.parse(message)); },
    close(_code?: number, _reason?: string) { port.disconnect(); },
  };
}
