import fs from 'node:fs'

export function isProcessAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false
  try {
    process.kill(pid, 0)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'EPERM') return false
  }
  try {
    const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8')
    // Field 3 is the state; "Z" is a zombie that no longer owns a browser window.
    const state = stat.slice(stat.lastIndexOf(')') + 2).split(' ')[0]
    return state !== 'Z' && state !== 'X'
  } catch {
    return false
  }
}

export function processExecutable(pid: number): string | null {
  try {
    return fs.readlinkSync(`/proc/${pid}/exe`)
  } catch {
    return null
  }
}

export function processCommandLine(pid: number): string | null {
  try {
    return fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\u0000').join(' ').trim()
  } catch {
    return null
  }
}

export function isExpectedProcess(pid: number, expectedExecutable: string): boolean {
  if (!isProcessAlive(pid)) return false
  const actual = processExecutable(pid)
  if (!actual) return false
  try {
    return fs.realpathSync(actual) === fs.realpathSync(expectedExecutable)
  } catch {
    return actual === expectedExecutable
  }
}

/**
 * Distro launchers (/usr/bin/chromium on Arch, snap wrappers) exec the real
 * browser binary, which changes /proc/<pid>/exe but keeps the pid. The profile
 * argument is the stable identity, so it is checked first.
 */
export function isInstanceProcess(pid: number, profilePath: string, expectedExecutable: string): boolean {
  if (!isProcessAlive(pid)) return false
  const commandLine = processCommandLine(pid)
  if (commandLine?.includes(`--user-data-dir=${profilePath}`)) return true
  return isExpectedProcess(pid, expectedExecutable)
}

function signal(pid: number, name: NodeJS.Signals): void {
  try {
    // Launches are detached, so the pid is also the process-group id.
    process.kill(-pid, name)
  } catch {
    try {
      process.kill(pid, name)
    } catch {
      /* already gone */
    }
  }
}

export async function terminateProcess(pid: number, timeoutMs = 2500): Promise<boolean> {
  if (!isProcessAlive(pid)) return true
  signal(pid, 'SIGTERM')
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (!isProcessAlive(pid)) return true
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  signal(pid, 'SIGKILL')
  const killDeadline = Date.now() + 1500
  while (Date.now() < killDeadline) {
    if (!isProcessAlive(pid)) return true
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  return !isProcessAlive(pid)
}

export function openInFileManager(target: string): void {
  // xdg-open is the only desktop-neutral entry point; failures are non-fatal.
  void import('node:child_process').then(({ spawn }) => {
    const child = spawn('xdg-open', [target], { detached: true, stdio: 'ignore' })
    child.unref()
  })
}
