import type { ZbaRuntimeLogFeed, ZbaRuntimeLogFeedEntry } from '../../api/zba'

export const LOG_CAPACITY = 600

/** Cursor progress and view-only clear are independent. Never clear server history. */
export class RuntimeLogBuffer {
  rows: ZbaRuntimeLogFeedEntry[] = []
  cursor: string | undefined
  private highWater = 0n
  private clearWatermark: bigint | undefined
  private establishClearBaseline = false

  clear(): void {
    this.rows = []
    this.clearWatermark = this.highWater
    this.establishClearBaseline = this.cursor === undefined
  }

  append(page: ZbaRuntimeLogFeed): { evicted: number; skippedReset: boolean } {
    const newest = page.items.at(-1)
    const restart = page.resetReason === 'restart'
    if (restart) {
      this.rows = []
      this.highWater = 0n
    }
    // Epochs have no comparable clock/sequence. After clear, skip a restart snapshot
    // (explicitly disclosed in the view), then receive only its subsequent deltas.
    const skippedReset = this.establishClearBaseline || (restart && this.clearWatermark !== undefined)
    if (skippedReset) {
      this.clearWatermark = newest ? BigInt(newest.sequence) : 0n
      // A compatible server may paginate its snapshot. Keep skipping until the
      // snapshot is drained, so continuation pages cannot revive cleared rows.
      this.establishClearBaseline = page.hasMore
    }
    const seen = new Set(this.rows.map(row => row.id))
    const accepted = skippedReset ? [] : page.items.filter(row => !seen.has(row.id)
      && (this.clearWatermark === undefined || BigInt(row.sequence) > this.clearWatermark))
    const combined = [...this.rows, ...accepted].sort((a, b) => {
      const left = BigInt(a.sequence), right = BigInt(b.sequence)
      return left < right ? -1 : left > right ? 1 : 0
    })
    const evicted = Math.max(0, combined.length - LOG_CAPACITY)
    this.rows = combined.slice(-LOG_CAPACITY)
    if (newest) this.highWater = this.highWater > BigInt(newest.sequence) ? this.highWater : BigInt(newest.sequence)
    this.cursor = page.cursor
    return { evicted, skippedReset }
  }
}
