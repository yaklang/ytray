import { browser } from 'wxt/browser';
import type { BridgeLaunchTarget } from '@/types/models';

import { hasNativeEngineTarget, normalizeLaunchTarget, YTRAY_ENGINE_HOST } from './launch-target';

export function nativeEngineOpen(target: BridgeLaunchTarget, purpose: 'pair' | 'connect') {
  return { type: 'yak_engine_open', purpose,
    ...(target.socketName ? { socketName: target.socketName } : { pipeName: target.pipeName }),
    engineIdentityId: target.engineIdentityId, engineInstanceId: target.engineInstanceId };
}

export async function discoverNativeEngines(): Promise<BridgeLaunchTarget[]> {
  try {
    const { os } = await browser.runtime.getPlatformInfo();
    if (os !== 'mac' && os !== 'win') return [];
    const result: unknown = await browser.runtime.sendNativeMessage(YTRAY_ENGINE_HOST, { type: 'yak_engine_discover' });
    if (!result || typeof result !== 'object' || !('targets' in result) || !Array.isArray(result.targets)) return [];
    return result.targets.map(normalizeLaunchTarget)
      .filter((target): target is BridgeLaunchTarget => target?.mode === 'engine' && Boolean(os === 'mac' ? target.socketName : target.pipeName));
  } catch {
    return [];
  }
}

export function selectNativeEngine(target: BridgeLaunchTarget, candidates: BridgeLaunchTarget[]): BridgeLaunchTarget {
  const matches = candidates.filter((candidate) =>
    candidate.engineIdentityId === target.engineIdentityId && candidate.frontendId === target.frontendId
    && hasNativeEngineTarget(candidate));
  const selected = matches.find((candidate) => candidate.engineInstanceId === target.engineInstanceId)
    || (matches.length === 1 ? matches[0] : undefined);
  if (!selected) throw new Error(matches.length
    ? `${target.frontendName} 有多个本机实例，请重新选择连接`
    : `等待 ${target.frontendName} 的本机连接`);
  return selected;
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
