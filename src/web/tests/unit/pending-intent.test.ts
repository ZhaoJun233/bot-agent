import { afterEach, describe, expect, it } from 'vitest'
import {
  activePending,
  beginPending,
  clearPending,
  discardPending,
  leavePendingUnknown,
  markPendingSent,
  PendingCapacityError,
  pendingByKey,
  pendingEntries,
  settlePending,
  serviceLabel
} from '../../src/features/tasks/pending-intent'

const SERVICE = 'http://localhost:3000|/zba/v1'

function begin(key: string, identity = 'idA', service = SERVICE) {
  return beginPending({ key, sourceKey: 'panel:synthetic', service, identity })
}

describe('pending-intent 登记处（R1）', () => {
  afterEach(() => clearPending())

  it('登记即占用入口，不需要等到失败', () => {
    const record = begin('task1-a')
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-a')
    expect(activePending(SERVICE, 'idA')?.phase).toBe('in_flight')
    expect(record.requestSent).toBe(false)
  })

  it('请求发出后标记 sent，仍占用入口', () => {
    const record = begin('task1-a')
    markPendingSent(record.token)
    const live = pendingByKey('task1-a')
    expect(live?.requestSent).toBe(true)
    expect(live?.phase).toBe('in_flight')
    expect(activePending(SERVICE, 'idA')).not.toBeNull()
  })

  it('迟到失败降级为 unresolved，仍然阻止新提交', () => {
    const record = begin('task1-a')
    markPendingSent(record.token)
    leavePendingUnknown(record.token, { taskId: 'zt-1' })
    const live = activePending(SERVICE, 'idA')
    expect(live?.phase).toBe('unresolved')
    expect(live?.taskId).toBe('zt-1')
  })

  it('拿到稳定 id 后释放入口', () => {
    const record = begin('task1-a')
    settlePending(record.token, 'resolved', 'zt-9')
    expect(activePending(SERVICE, 'idA')).toBeNull()
    // 已解决的记录不占用入口，但记录本身还在：页面上的那一行仍能按 key 查回它。
    expect(pendingByKey('task1-a')?.phase).toBe('resolved')
  })

  it('明确拒绝释放入口，但终态结论保留下来（M2）', () => {
    const record = begin('task1-a')
    settlePending(record.token, 'rejected')
    expect(activePending(SERVICE, 'idA')).toBeNull()
    // 终态要留给重连后的恢复行，否则那一行永远停在"正在等待受理…"。
    expect(pendingByKey('task1-a')?.phase).toBe('rejected')
  })

  it('显式放弃才真正移除记录', () => {
    const record = begin('task1-a')
    settlePending(record.token, 'rejected')
    discardPending('task1-a')
    expect(pendingEntries()).toHaveLength(0)
    expect(activePending(SERVICE, 'idA')).toBeNull()
  })

  it('终态记录不再占用提交入口，新的意图可以立刻提交', () => {
    const first = begin('task1-a')
    settlePending(first.token, 'resolved', 'zt-1')
    expect(activePending(SERVICE, 'idA')).toBeNull()
    const second = begin('task1-b')
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-b')
    expect(pendingByKey('task1-a')?.phase).toBe('resolved')
    expect(pendingByKey('task1-a')?.taskId).toBe('zt-1')
    void second
  })

  it('身份隔离：B 身份看不到 A 身份的未决关联', () => {
    const record = begin('task1-a', 'idA')
    leavePendingUnknown(record.token)
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-a')
    expect(activePending(SERVICE, 'idB')).toBeNull()
  })

  it('服务隔离：换基址后旧标识不可见，也不会被发给新服务', () => {
    const other = 'http://localhost:3000|/zba/v2'
    const record = begin('task1-a', 'idA', other)
    leavePendingUnknown(record.token)
    expect(activePending(SERVICE, 'idA')).toBeNull()
    expect(activePending(other, 'idA')?.key).toBe('task1-a')
  })

  it('同键重复登记只占一个名额，并且新记录覆盖旧的', () => {
    const first = begin('task1-a')
    const second = begin('task1-a')
    expect(pendingEntries()).toHaveLength(1)
    expect(activePending(SERVICE, 'idA')?.token).toBe(second.token)
    // 旧句柄的回写不应影响当前记录。
    settlePending(first.token, 'resolved')
    expect(activePending(SERVICE, 'idA')?.token).toBe(second.token)
  })

  it('登记处只保留最小关联信息，不含 prompt 与请求体', () => {
    const record = begin('task1-a')
    const keys = Object.keys(record).sort()
    expect(keys).toEqual([
      'bornOf', 'findCode', 'identity', 'key', 'phase', 'requestSent',
      'savedAt', 'service', 'sourceKey', 'taskId', 'token'
    ])
    const serialized = JSON.stringify(pendingEntries())
    expect(serialized).not.toContain('prompt')
    expect(serialized).not.toContain('body')
  })

  // ── CAPACITY-M1：终态与未决不共享容量 ────────────────────────────
  it('未决容量满时拒绝新登记，绝不静默淘汰未决关联（CAPACITY-M1）', () => {
    for (let index = 0; index < 8; index += 1) begin('task1-' + index)
    expect(pendingEntries()).toHaveLength(8)
    // 第 9 条：满额就拒绝，绝不挤掉最旧的未决关联（它可能已被服务端受理）。
    expect(() => begin('task1-8')).toThrow(PendingCapacityError)
    expect(pendingEntries()).toHaveLength(8)
    expect(pendingByKey('task1-0')).not.toBeNull()
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-7')
  })

  it('未决名额按身份划分：一个身份占满只停用它自己的入口（CAPACITY-M1）', () => {
    for (let index = 0; index < 8; index += 1) begin('task1-a' + index, 'idA')
    expect(() => begin('task1-a8', 'idA')).toThrow(PendingCapacityError)
    // 别的身份完全不受影响：既不会被停用，也不会挤掉 A 的那八条。
    expect(() => begin('task1-b0', 'idB')).not.toThrow()
    expect(activePending(SERVICE, 'idB')?.key).toBe('task1-b0')
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-a7')
    expect(pendingByKey('task1-a0')).not.toBeNull()
  })

  it('未决名额按服务划分：换基址后的新服务另有名额', () => {
    const other = 'http://localhost:3000|/zba/v2'
    for (let index = 0; index < 8; index += 1) begin('task1-s' + index, 'idA', other)
    expect(() => begin('task1-s8', 'idA', other)).toThrow(PendingCapacityError)
    expect(() => begin('task1-own0')).not.toThrow()
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-own0')
  })

  it('终态不占用未决容量：别的身份连续明确拒绝挤不掉未决关联（CAPACITY-M1）', () => {
    const a = begin('task1-a', 'idA')
    leavePendingUnknown(a.token)
    // B 身份连续 12 次明确拒绝：终态记录再多也不影响 A 的未决名额。
    for (let index = 0; index < 12; index += 1) {
      const b = begin('task1-b' + index, 'idB')
      settlePending(b.token, 'rejected')
    }
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-a')
    expect(pendingByKey('task1-a')?.phase).toBe('unresolved')
    // 终态按自己的独立上限保留，不是无限堆积。
    expect(pendingEntries().filter((item) => item.phase === 'rejected').length)
      .toBeLessThanOrEqual(8)
  })

  it('终态记录有独立上限：超出时先淘汰最旧的终态', () => {
    const first = begin('task1-0')
    settlePending(first.token, 'resolved', 'zt-0')
    for (let index = 1; index <= 8; index += 1) {
      const record = begin('task1-' + index)
      settlePending(record.token, 'resolved', 'zt-' + index)
    }
    // 9 条终态 → 上限 8：最旧的那条被淘汰，最新的一定还在。
    expect(pendingEntries().filter((item) => item.phase === 'resolved')).toHaveLength(8)
    expect(pendingByKey('task1-0')).toBeNull()
    expect(pendingByKey('task1-8')).not.toBeNull()
  })

  it('同一毫秒内登记的多个未决关联，取最新一条', () => {
    // savedAt 相同（同一毫秒）时不能靠字符串比较走到列表末尾——那是最旧的一条。
    const a = begin('task1-a')
    const b = begin('task1-b')
    leavePendingUnknown(a.token)
    leavePendingUnknown(b.token)
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-b')
  })

  it('终态记录的结论要能传下去（M2）', () => {
    const first = begin('task1-a')
    markPendingSent(first.token)
    settlePending(first.token, 'resolved', 'zt-9')
    const settled = pendingByKey('task1-a')
    expect(settled?.phase).toBe('resolved')
    expect(settled?.taskId).toBe('zt-9')
    expect(activePending(SERVICE, 'idA')).toBeNull()
  })

  it('明确拒绝也保留结论，供重连后的恢复行使用（M2）', () => {
    const record = begin('task1-a')
    markPendingSent(record.token)
    settlePending(record.token, 'rejected')
    expect(pendingByKey('task1-a')?.phase).toBe('rejected')
    expect(activePending(SERVICE, 'idA')).toBeNull()
  })

  it('显式放弃会移除记录，不再向恢复行传播（M2）', () => {
    const record = begin('task1-a')
    settlePending(record.token, 'rejected')
    discardPending('task1-a')
    expect(pendingByKey('task1-a')).toBeNull()
  })

  it('终态记录不占用入口，新意图照常登记（M2）', () => {
    const first = begin('task1-a')
    settlePending(first.token, 'resolved', 'zt-1')
    const second = begin('task1-b')
    markPendingSent(second.token)
    expect(pendingByKey('task1-a')?.phase).toBe('resolved')
    expect(pendingByKey('task1-b')?.phase).toBe('in_flight')
    expect(activePending(SERVICE, 'idA')?.key).toBe('task1-b')
  })
  it('serviceLabel 把同源与基址一起纳入，不同基址互不可见', () => {
    expect(serviceLabel({ baseUrl: '/zba/v1' })).toBe(SERVICE)
    expect(serviceLabel({ baseUrl: '/zba/v2' })).not.toBe(SERVICE)
  })
})