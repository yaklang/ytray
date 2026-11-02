#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import url from 'node:url'

const here = path.dirname(url.fileURLToPath(import.meta.url))
const root = path.resolve(here, '..')
const versionFile = path.resolve(root, '..', 'VERSION')
const packageFile = path.join(root, 'package.json')

const version = fs.readFileSync(versionFile, 'utf8').trim()
if (!/^\d+(\.\d+)*$/.test(version)) {
  console.error(`VERSION contains an invalid value: ${version}`)
  process.exit(1)
}

const manifest = JSON.parse(fs.readFileSync(packageFile, 'utf8'))
manifest.version = version
fs.writeFileSync(packageFile, `${JSON.stringify(manifest, null, 2)}\n`)
console.log(`linux/package.json version -> ${version}`)
