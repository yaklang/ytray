import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

// Execute the actual listener bodies from both generated-extension templates.
for (const file of ['../windows/src/Core/ProxyAuthenticationExtension.cs', '../darwin/Sources/YTray/ProxyAuthenticationExtension.swift']) {
  const source = await readFile(new URL(file, import.meta.url), 'utf8');
  let script = source.slice(source.indexOf('chrome.webRequest.onAuthRequired.addListener('));
  script = script.slice(0, script.indexOf('chrome.webRequest.onErrorOccurred.addListener') + script.slice(script.indexOf('chrome.webRequest.onErrorOccurred.addListener')).indexOf(';') + 1);
  script = file.endsWith('.cs') ? script.replaceAll('{{', '{').replaceAll('}}', '}').replaceAll('""', '"') : script.replaceAll('\\\\', '\\');
  for (const host of ['proxy.example', '::1']) {
    let authenticate, complete;
    runInNewContext(script, {
      username: 'test-user', password: 'test-password', proxyHost: host, proxyPort: 8083, attempts: new Map(),
      chrome: { webRequest: {
        onAuthRequired: { addListener: (listener) => { authenticate = listener; } },
        onCompleted: { addListener: (listener) => { complete = listener; } },
        onErrorOccurred: { addListener: () => {} },
      } },
    });
    const invoke = (details) => {
      let result;
      authenticate({ requestId: 'request', ...details }, (value) => { result = JSON.parse(JSON.stringify(value)); });
      return result;
    };
    assert.deepEqual(invoke({ isProxy: false, challenger: { host, port: 8083 } }), {});
    assert.deepEqual(invoke({ isProxy: true }), {});
    assert.deepEqual(invoke({ isProxy: true, challenger: { host: 'other.example', port: 8083 } }), {});
    assert.deepEqual(invoke({ isProxy: true, challenger: { host, port: 9999 } }), {});
    const challenge = { isProxy: true, challenger: { host: host.includes(':') ? `[${host}]` : host.toUpperCase(), port: 8083 } };
    assert.deepEqual(invoke(challenge), { authCredentials: { username: 'test-user', password: 'test-password' } });
    assert.deepEqual(invoke(challenge), { cancel: true });
    complete({ requestId: 'request' });
    assert.ok(invoke(challenge).authCredentials);
  }
  console.log(`PASS ${file}: endpoint isolation, missing challenger, IPv6, case, retries, cleanup`);
}
