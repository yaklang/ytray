import fs from 'node:fs'
import path from 'node:path'
import { proxyAuthDirectory } from './paths'
import { splitProxy } from './proxy-address'
import { log } from './diagnostics'

/**
 * Generates a throwaway MV3 extension that answers proxy 407 challenges.
 * Chromium's command line cannot carry proxy credentials, so every authenticated
 * launch gets its own extension directory with 0700/0600 permissions.
 */
export function writeProxyAuthExtension(
  instanceId: string,
  username: string,
  password: string,
  proxyServer: string
): string | null {
  if (!username && !password) return null
  const endpoint = splitProxy(proxyServer)
  const directory = path.join(proxyAuthDirectory(), instanceId)
  fs.rmSync(directory, { recursive: true, force: true })
  fs.mkdirSync(directory, { recursive: true, mode: 0o700 })
  fs.chmodSync(directory, 0o700)

  const manifest = {
    manifest_version: 3,
    name: 'YTray Proxy Authentication',
    version: '1.0.0',
    permissions: ['webRequest', 'webRequestAuthProvider'],
    host_permissions: ['<all_urls>'],
    background: { service_worker: 'background.js' }
  }

  const background = `const username = ${JSON.stringify(username)};
const password = ${JSON.stringify(password)};
const proxyHost = ${JSON.stringify(endpoint.host.toLowerCase())};
const proxyPort = ${endpoint.port};
const attempts = new Map();

chrome.webRequest.onAuthRequired.addListener(
  (details, callback) => {
    if (!details.isProxy || String(details.challenger?.host || '').toLowerCase().replace(/^\\[|\\]$/g, '') !== proxyHost
        || details.challenger?.port !== proxyPort) { callback({}); return; }
    const count = (attempts.get(details.requestId) || 0) + 1;
    attempts.set(details.requestId, count);
    if (count > 1) { callback({ cancel: true }); return; }
    callback({ authCredentials: { username, password } });
  },
  { urls: ['<all_urls>'] },
  ['asyncBlocking']
);

const clearAttempt = details => attempts.delete(details.requestId);
chrome.webRequest.onCompleted.addListener(clearAttempt, { urls: ['<all_urls>'] });
chrome.webRequest.onErrorOccurred.addListener(clearAttempt, { urls: ['<all_urls>'] });
`

  fs.writeFileSync(path.join(directory, 'manifest.json'), JSON.stringify(manifest, null, 2), { mode: 0o600 })
  fs.writeFileSync(path.join(directory, 'background.js'), background, { mode: 0o600 })
  return directory
}

export function removeProxyAuthExtension(instanceId: string): void {
  try {
    fs.rmSync(path.join(proxyAuthDirectory(), instanceId), { recursive: true, force: true })
  } catch (error) {
    log.error('proxy.auth.cleanup', error, instanceId)
  }
}
