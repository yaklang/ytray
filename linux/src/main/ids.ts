import { randomUUID } from 'node:crypto'

/** UUID v4 string, matching the identifier shape the other two clients persist. */
export function ytrayId(): string {
  return randomUUID()
}
