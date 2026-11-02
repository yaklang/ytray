import { net, protocol } from 'electron'
import fs from 'node:fs'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { dataDirectory, picturesDirectory } from './paths'
import { log } from './diagnostics'

/**
 * Renderer pages cannot load file:// images in development (http origin) and
 * should never get a blanket file:// grant, so screenshots and thumbnails are
 * served through a read-only custom scheme restricted to the app's own data.
 */
export const MEDIA_SCHEME = 'ytray-media'

export function registerMediaScheme(): void {
  protocol.registerSchemesAsPrivileged([
    { scheme: MEDIA_SCHEME, privileges: { standard: true, secure: true, supportFetchAPI: true } }
  ])
}

function allowedRoots(): string[] {
  return [path.resolve(dataDirectory()), path.resolve(picturesDirectory())]
}

function isAllowed(file: string): boolean {
  const resolved = path.resolve(file)
  return allowedRoots().some((root) => resolved === root || resolved.startsWith(root + path.sep))
}

export function handleMediaProtocol(): void {
  protocol.handle(MEDIA_SCHEME, async (request) => {
    try {
      const url = new URL(request.url)
      const file = url.searchParams.get('path')
      if (!file || !isAllowed(file) || !fs.existsSync(file)) {
        return new Response('Not found', { status: 404 })
      }
      return await net.fetch(pathToFileURL(file).toString())
    } catch (error) {
      log.error('media.protocol', error)
      return new Response('Bad request', { status: 400 })
    }
  })
}

export function mediaUrl(file: string): string {
  return `${MEDIA_SCHEME}://local/?path=${encodeURIComponent(file)}`
}
