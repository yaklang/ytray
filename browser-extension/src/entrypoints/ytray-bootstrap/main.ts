import { browser } from 'wxt/browser';
import { request } from '@/platform/messaging/runtime';
import { ExtensionError } from '@/shared/errors';

const status = document.getElementById('status');
const fail = (message: string) => {
  if (status) status.textContent = message;
};

async function bootstrap(): Promise<void> {
  const query = new URLSearchParams(location.search);
  if (query.get('connectionOnly') === '1') return;
  const manager = query.get('manager');
  const instanceId = query.get('instanceId') || '';
  const badge = query.get('badge') || '';
  const target = query.get('target') || 'chrome://newtab/';
  const browserUseTicket = query.get('browserUseTicket') || undefined;
  const engineTargetV2 = query.get('engineTargetV2');
  const engineTarget = query.get('engineTarget');
  if (!['ytray', 'yakit'].includes(manager || '')
    || !/^[A-Za-z0-9-]{1,160}$/.test(instanceId)
    || !/^[A-Z]{1,2}$/.test(badge)) {
    throw new Error('浏览器实例身份参数无效');
  }
  const protocol = new URL(target).protocol;
  if (!['http:', 'https:', 'chrome:'].includes(protocol)
    && target !== 'data:text/html,<title>YTray</title>') {
    throw new Error('浏览器实例目标地址无效');
  }
  if (browserUseTicket) {
    const current = new URL(location.href);
    current.searchParams.delete('browserUseTicket');
    history.replaceState(null, '', current);
  }

  const binding = {
    manager: manager as 'ytray' | 'yakit', instanceId, badge,
    browserName: query.get('browserName') || undefined,
    browserVersion: query.get('browserVersion') || undefined,
    startupProxy: query.get('startupProxy') || undefined,
    browserUseTicket,
  };
  let bound = false;
  if (engineTargetV2 !== null) {
    try {
      await request('bridge.managed-instance.bind.v2', { ...binding, launchTarget: JSON.parse(engineTargetV2) });
      bound = true;
    } catch (error) {
      if (!(error instanceof ExtensionError) || error.code !== 'request_failed' || error.message !== '未知扩展操作') throw error;
    }
  }
  if (!bound) await request('bridge.managed-instance.bind', {
    ...binding, launchTarget: engineTarget === null ? undefined : JSON.parse(engineTarget),
  });

  const current = await browser.tabs.getCurrent();
  if (!current?.id) {
    location.replace(target);
    return;
  }
  if (query.get('restore') === '1') {
    await new Promise((resolve) => globalThis.setTimeout(resolve, 400));
    const tabs = await browser.tabs.query({ currentWindow: true });
    if (tabs.some((tab) => tab.id !== current.id)) {
      await browser.tabs.remove(current.id);
      return;
    }
  }
  await browser.tabs.update(current.id, { url: target });
}

void bootstrap().catch((error) => fail(error instanceof Error ? error.message : String(error)));
