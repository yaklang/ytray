#!/usr/bin/env node
import { execFileSync, execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const extension = fileURLToPath(new URL('../browser-extension/', import.meta.url));
const variant = 'chrome-enterprise';
const archiveName = 'yakit-browser-agent.zip';
const descriptorName = 'bundled-extension.json';
const readJson = async (path) => JSON.parse(await readFile(path, 'utf8'));

async function buildBundle(dist, version) {
  const options = { cwd: extension, stdio: 'inherit' };
  execSync('pnpm install --frozen-lockfile', options);
  const env = { ...process.env };
  delete env.YTRAY_BROWSER_TEST_PORT;
  execSync('pnpm build:enterprise', { ...options, env });
  execFileSync(process.execPath, [
    'scripts/package-release.mjs',
    `--dist=${dist}`,
    `--variant=${variant}`,
    '--public-base-url=https://aliyun-oss.yaklang.com/chrome-extension',
  ], options);

  const release = await readJson(join(dist, 'release-entry.json'));
  const artifact = release.artifacts.find((entry) => entry.variant === variant);
  const filename = `${variant}-${version}.zip`;
  if (!artifact || artifact.filename !== filename) throw new Error(`Missing ${filename} in extension package output`);
  return {
    archive: await readFile(join(dist, version, filename)),
    descriptor: {
      version: release.version,
      variant,
      size: artifact.size,
      sha256: artifact.sha256,
      sourceCommit: release.commit,
    },
  };
}

if (process.argv.length !== 3) throw new Error('Usage: node script/prepare-yakit-browser-agent.mjs OUTPUT_DIR');
const output = resolve(process.argv[2]);
const { version } = await readJson(join(extension, 'package.json'));
const prebuilt = process.env.YTRAY_EXTENSION_BUNDLE_DIRECTORY;
const scratch = prebuilt ? undefined : await mkdtemp(join(tmpdir(), 'ytray-extension-'));
try {
  const { archive, descriptor } = prebuilt
    ? {
        archive: await readFile(join(prebuilt, archiveName)),
        descriptor: await readJson(join(prebuilt, descriptorName)),
      }
    : await buildBundle(scratch, version);
  if (descriptor.variant !== variant || descriptor.version !== version) {
    throw new Error(`Expected bundled extension ${variant} ${version}`);
  }
  if (process.env.GITHUB_SHA && descriptor.sourceCommit !== process.env.GITHUB_SHA) {
    throw new Error('Bundled extension was built from a different commit');
  }
  const digest = createHash('sha256').update(archive).digest('hex');
  if (archive.length === 0 || archive.length !== descriptor.size || digest !== descriptor.sha256) {
    throw new Error('Bundled extension size or SHA-256 does not match its descriptor');
  }

  await mkdir(output, { recursive: true });
  await writeFile(join(output, archiveName), archive);
  await writeFile(join(output, descriptorName), `${JSON.stringify(descriptor, null, 2)}\n`);
  console.log(`Prepared Yakit Browser Agent ${version} (${archive.length} bytes) in ${output}`);
} finally {
  if (scratch) await rm(scratch, { recursive: true, force: true });
}
