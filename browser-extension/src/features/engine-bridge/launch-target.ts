import type { BridgeConfig, BridgeLaunchTarget, DiscoveredYakEngine } from '@/types/models';
export const YTRAY_ENGINE_HOST = 'com.yaklang.ytray_engine';

export const launchFrontendId = (target?: BridgeLaunchTarget) => target?.ownerFrontendId || target?.frontendId;

export function validEnginePipe(pipe: unknown, instanceId: unknown): pipe is string {
  return typeof instanceId === 'string' && /^engine-[a-f0-9]{32}$/.test(instanceId)
    && pipe === 'yak-browser-bridge-v1-' + instanceId;
}

export function hasNativeEngineTarget(target?: BridgeLaunchTarget): boolean {
  return Boolean(target?.pipeName || target?.socketName);
}

export function validEngineSocket(socket: unknown, instanceId: unknown): socket is string {
  return typeof instanceId === 'string' && /^engine-[a-f0-9]{32}$/.test(instanceId)
    && socket === 'yak-browser-bridge-v1-' + instanceId + '.sock';
}

export function compatibleEngineName(engine: DiscoveredYakEngine): string {
  if (engine.ownerFrontendId === 'memfit') return 'Memfit AI';
  if (engine.ownerFrontendId === 'yakit') return 'Yakit';
  return engine.frontendName ? `${engine.frontendName}（引擎自报）` : '本机 Yak 引擎';
}

export function webSocketLaunchTarget(engine: DiscoveredYakEngine): BridgeLaunchTarget {
  return { mode: 'engine', exactInstance: true, endpoint: engine.endpoint, challengeOnly: engine.challengeOnly,
    ownerFrontendId: engine.ownerFrontendId,
    engineIdentityId: engine.engineIdentityId, engineInstanceId: engine.engineInstanceId,
    frontendId: engine.frontendId || 'yak', frontendName: compatibleEngineName(engine) };
}

export function bindLaunchTarget(config: BridgeConfig, target: BridgeLaunchTarget): BridgeConfig {
  // A launcher without discovery support must preserve the user's existing connection and credentials.
  if (target.mode === 'legacy') return { ...config, launchTarget: { mode: 'legacy' }, automaticWebSocketFallback: undefined,
    autoConnect: config.launchTarget?.mode === 'none' ? true : config.autoConnect };
  if (target.mode === 'auto' && hasNativeEngineTarget(config.launchTarget)) return config;
  if (target.mode === 'auto' && config.transport === 'websocket'
    && (config.pairedEngine || config.launchTarget?.mode === 'legacy')) {
    return {
      ...config,
      launchTarget: config.launchTarget?.mode === 'engine' ? config.launchTarget : { mode: 'legacy' },
      autoConnect: config.launchTarget?.mode === 'none' ? true : config.autoConnect,
    };
  }
  const previous = config.pairedEngine && { ...config.pairedEngine,
    frontendId: config.pairedEngine.frontendId || launchFrontendId(config.launchTarget)
      || (target.mode === 'engine' && config.endpoint === target.endpoint ? launchFrontendId(target) : undefined) };
  const records = [...new Map([...(config.trustedEngines || []), ...(previous ? [previous] : [])]
    .map((engine) => [`${engine.engineIdentityId}:${engine.frontendId || ''}`, engine])).values()].slice(-32);
  if (target.mode === 'auto') return { ...config, launchTarget: target, transport: 'native', automaticWebSocketFallback: undefined,
    nativeHost: YTRAY_ENGINE_HOST, autoConnect: true, trustedEngines: records, pairedEngine: undefined };
  return { ...config, launchTarget: target, automaticWebSocketFallback: undefined,
    transport: target.mode === 'engine' ? hasNativeEngineTarget(target) ? 'native' : 'websocket' : config.transport,
    autoConnect: target.mode === 'engine',
    nativeHost: target.mode === 'engine' && hasNativeEngineTarget(target) ? YTRAY_ENGINE_HOST : config.nativeHost,
    endpoint: target.mode === 'engine' && target.endpoint ? target.endpoint : config.endpoint,
    trustedEngines: records,
    pairedEngine: target.mode === 'engine' ? records.find((engine) => engine.engineIdentityId === target.engineIdentityId
      && engine.frontendId === launchFrontendId(target)) : previous,
  };
}

export function normalizeLaunchTarget(value: unknown): BridgeLaunchTarget | undefined {
  if (value === undefined) return undefined;
  if (!value || typeof value !== 'object' || Array.isArray(value)) return { mode: 'none' };
  const target = value as Record<string, unknown>;
  if (target.mode === 'legacy') return { mode: 'legacy' };
  if (target.mode === 'auto') return { mode: 'auto' };
  if (target.mode === 'none') return { mode: 'none' };
  if (target.mode !== 'engine') return { mode: 'none' };
  for (const key of ['engineIdentityId', 'engineInstanceId', 'frontendId', 'frontendName']) {
    if (typeof target[key] !== 'string' || !target[key] || (target[key] as string).length > 160) return { mode: 'none' };
  }
  const pipe = validEnginePipe(target.pipeName, target.engineInstanceId) && target.exactInstance === true;
  const socket = validEngineSocket(target.socketName, target.engineInstanceId) && target.exactInstance === true;
  if (target.pipeName !== undefined || target.socketName !== undefined) {
    if ((!pipe && !socket) || (target.pipeName !== undefined && target.socketName !== undefined)
      || target.endpoint !== undefined || target.challengeOnly) return { mode: 'none' };
  } else {
    if (typeof target.endpoint !== 'string' || target.endpoint.length > 2048) return { mode: 'none' };
    try {
      const url = new URL(target.endpoint);
      if (!['ws:', 'wss:'].includes(url.protocol) || !['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)
        || url.pathname !== '/extension' || url.username || url.password || url.search || url.hash) return { mode: 'none' };
    } catch { return { mode: 'none' }; }
  }
  return {
    mode: 'engine', engineIdentityId: target.engineIdentityId as string, engineInstanceId: target.engineInstanceId as string,
    frontendId: target.frontendId as string,
    ...(target.exactInstance === true ? { exactInstance: true } : {}),
    ...(target.exactInstance === true && target.challengeOnly === true ? { challengeOnly: true } : {}),
    ...(target.ownerFrontendId === 'memfit' || target.ownerFrontendId === 'yakit' ? { ownerFrontendId: target.ownerFrontendId } : {}),
    frontendName: target.frontendName as string,
    ...(pipe ? { pipeName: target.pipeName as string } : socket ? { socketName: target.socketName as string } : { endpoint: target.endpoint as string }),
  };
}

export function selectLaunchTarget(target: BridgeLaunchTarget, engines: DiscoveredYakEngine[]): DiscoveredYakEngine {
  const matches = engines.filter((engine) => engine.engineIdentityId === target.engineIdentityId
    && (engine.frontendId || 'yak') === target.frontendId
    && (!target.ownerFrontendId || (engine.ownerFrontendId || engine.frontendId) === target.ownerFrontendId
      || (!engine.ownerFrontendId && engine.endpoint === target.endpoint && engine.engineInstanceId === target.engineInstanceId)));
  const exact = matches.find((engine) => engine.engineInstanceId === target.engineInstanceId);
  if (exact) return exact;
  if (matches.length === 1 && !target.exactInstance && !target.ownerFrontendId) return matches[0];
  throw new Error(matches.length > 1
    ? `${target.frontendName} 有多个引擎实例，请在 YTray 中重新选择接入目标`
    : `等待所选的 ${target.frontendName} 引擎启动；不会自动连接其他引擎`);
}
