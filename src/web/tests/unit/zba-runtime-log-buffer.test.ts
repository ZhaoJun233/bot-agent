import { describe, expect, it } from 'vitest'
import type { ZbaRuntimeLogFeed } from '../../src/api/zba'
import { RuntimeLogBuffer } from '../../src/features/logs/runtime-log-buffer'
const epoch = 'a'.repeat(32)
function page(from: number, count: number, origin = epoch): ZbaRuntimeLogFeed {
  return { capacity: 600, cursor: origin + ':' + (from + count - 1), hasMore: false, items: Array.from({ length: count }, (_, i) => ({
    id: origin + ':' + (from + i), sequence: String(from + i), time: 1793773800000,
    level: 'info', source: 'Web', message: '安全摘要', redacted: true
  })) }
}
describe('安全日志缓冲和本地清空水位', () => {
  it('稳定 ID 去重，按序排列，最多600行', () => {
    const buffer = new RuntimeLogBuffer()
    buffer.append(page(201, 200)); buffer.append(page(1, 200)); buffer.append(page(201, 200))
    expect(buffer.rows).toHaveLength(400)
    expect(buffer.rows[0]!.sequence).toBe('1')
    buffer.append(page(401, 200))
    expect(buffer.append(page(601, 200)).evicted).toBe(200)
    expect(buffer.rows).toHaveLength(600)
    expect(buffer.rows[0]!.sequence).toBe('201')
    expect(buffer.rows.at(-1)!.sequence).toBe('800')
  })
  it('清空不丢游标，重连/过期重置不复活清空前行', () => {
    const buffer = new RuntimeLogBuffer()
    buffer.append(page(1, 3)); buffer.clear()
    expect(buffer.cursor).toBe(epoch + ':3')
    buffer.append({ ...page(1, 5), resetReason: 'expired', omittedCount: 1 })
    expect(buffer.rows.map(row => row.sequence)).toEqual(['4', '5'])
  })
  it('重启不可跨 epoch 比较，跳过快照后继续新增量', () => {
    const buffer = new RuntimeLogBuffer()
    buffer.append(page(1, 3)); buffer.clear()
    const newEpoch = 'b'.repeat(32)
    expect(buffer.append({ ...page(1, 2, newEpoch), resetReason: 'restart' }).skippedReset).toBe(true)
    expect(buffer.rows).toHaveLength(0)
    buffer.append(page(3, 1, newEpoch))
    expect(buffer.rows.map(row => row.id)).toEqual([newEpoch + ':3'])
  })
  it.each([false, true])('清空后的分页快照全部跳过（重启=%s），只显示后续增量', restart => {
    const buffer = new RuntimeLogBuffer()
    if (restart) buffer.append(page(1, 3))
    buffer.clear()
    const origin = restart ? 'b'.repeat(32) : epoch
    buffer.append({ ...page(1, 200, origin), hasMore: true, ...(restart ? { resetReason: 'restart' as const } : {}) })
    buffer.append({ ...page(201, 200, origin), hasMore: true })
    buffer.append(page(401, 200, origin))
    expect(buffer.rows).toHaveLength(0)
    expect(buffer.cursor).toBe(origin + ':600')
    buffer.append(page(601, 1, origin))
    expect(buffer.rows.map(row => row.sequence)).toEqual(['601'])
  })
  it('首次响应到达前清空，不把初始快照回填', () => {
    const buffer = new RuntimeLogBuffer()
    buffer.clear()
    expect(buffer.append(page(1, 3)).skippedReset).toBe(true)
    expect(buffer.rows).toHaveLength(0)
    buffer.append(page(4, 1))
    expect(buffer.rows[0]?.sequence).toBe('4')
  })
})
