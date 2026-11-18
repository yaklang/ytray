import type { BridgeStatus, ExtensionState } from '@/types/models';

const TRANSPORT_LABELS = {
  websocket: '本机 WebSocket',
  native: 'Native Messaging',
  'named-pipe': 'Windows 命名管道',
  'unix-socket': 'Unix Socket',
} satisfies Record<NonNullable<BridgeStatus['transport']>, string>;

export function connectedTransportLabel(bridge: BridgeStatus): string | undefined {
  return bridge.state === 'connected' && bridge.transport ? TRANSPORT_LABELS[bridge.transport] : undefined;
}

export function connectedEngineName(state: ExtensionState, bridge: BridgeStatus): string {
  const target = state.bridge.launchTarget;
  if (target?.mode === 'engine') {
    const matches = target.engineIdentityId === bridge.engineIdentityId
      && (!target.exactInstance || target.engineInstanceId === bridge.engineInstanceId);
    return matches && target.frontendName ? target.frontendName : 'Yak 引擎';
  }
  const paired = state.bridge.pairedEngine;
  if (paired?.engineIdentityId === bridge.engineIdentityId) {
    if (paired?.frontendId === 'memfit') return 'Memfit AI';
    if (paired?.frontendId === 'yakit') return 'Yakit';
    if (paired?.frontendId === 'yakit-enterprise') return 'Yakit 企业版';
    if (paired?.frontendId === 'irify') return 'IRify';
  }
  return 'Yak 引擎';
}
