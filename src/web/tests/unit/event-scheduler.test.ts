import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EventScheduler } from '../../src/shared/sync/event-scheduler'
import type { ZbaClient, ZbaEvent, ZbaEventPage } from '../../src/api/zba'

/**
 * 共享事件调度器（阶段计划 §9.3）。
 *
 * 这些用例覆盖验收报告 S3-06 要求的每一项算法：共享/单表、sequence 去重、
 * 游标锚点、resync 补读顺序、失败保持旧数据与退避、身份释放后不复用游标。
 */

function event(sequence: number, type = 'task.updated', extra: Partial<ZbaEvent> = {}): ZbaEvent {
  return { sequence, type, at: '2026-10-23T00:00:00Z', ...extra }
}

/** 可编程的假 client：每次 listEvents 按脚本返回一页。 */
function fakeClient(pages: Array<ZbaEventPage | Error>): ZbaClient & { calls: number[] } {
  const calls: number[] = []
  const client = {
    baseUrl: '/zba/v1',
    identityLabel: 'x',
    calls,
    listEvents: async (after: number) => {
      calls.push(after)
      const next = pages.shift()
      if (!next) return { items: [], nextSequence: after, resyncRequired: false, generatedAt: 'x' }
      if (next instanceof Error) throw next
      return next
    }
  } as unknown as ZbaClient & { calls: number[] }
  return client
}

function page(items: ZbaEvent[], nextSequence: number, resyncRequired = false): ZbaEventPage {
  return { items, nextSequence, resyncRequired, generatedAt: '2026-10-23T00:00:00Z' }
}

describe('EventScheduler', () => {
  it('初次同步整份重读并按锚点推进游标，不丢事件', async () => {
    const client = fakeClient([
      page([], 7, true),
      page([event(8, 'task.updated', { taskId: 't-1' })], 8)
    ])
    const scheduler = new EventScheduler(client, { isHidden: () => false })
    const reloads: string[] = []
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => { reloads.push('tasks') } })

    expect(await scheduler.pollOnce()).toBe(true)
    // 初次同步：先记锚点(7)，整份重读，游标=7
    expect(reloads).toEqual(['tasks'])
    expect(scheduler.snapshot().cursor).toBe(7)

    await scheduler.pollOnce()
    // 之后的事件只补读一次，游标追到 8
    expect(reloads).toEqual(['tasks', 'tasks'])
    expect(scheduler.snapshot().cursor).toBe(8)
    scheduler.stop()
  })

  it('同一 sequence 不重复处理，乱序事件按序号处理', async () => {
    const client = fakeClient([
      page([], 0, true),
      page([event(3), event(1), event(2), event(2)], 3),
      page([event(3), event(2)], 3)
    ])
    const scheduler = new EventScheduler(client, { isHidden: () => false })
    const seen: number[] = []
    scheduler.subscribe({
      name: 'tasks',
      matches: () => true,
      reload: async () => { seen.push(scheduler.snapshot().cursor) }
    })
    await scheduler.pollOnce()
    await scheduler.pollOnce()
    await scheduler.pollOnce()
    // 第二轮处理了一次（同一轮只重读一次），第三轮全是旧序号 → 不重读
    expect(seen).toHaveLength(2)
    expect(scheduler.snapshot().cursor).toBe(3)
    scheduler.stop()
  })

  it('补读失败保持旧数据、标记 stale 并退避，不推进游标', async () => {
    const client = fakeClient([
      page([], 5, true),
      new Error('boom'),
      page([], 5)
    ])
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 1000, maxBackoffMs: 8000 })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    await scheduler.pollOnce()
    expect(scheduler.snapshot().cursor).toBe(5)

    expect(await scheduler.pollOnce()).toBe(false)
    expect(scheduler.snapshot().state).toBe('stale')
    expect(scheduler.snapshot().failures).toBe(1)
    // 游标没有因为失败而前进
    expect(scheduler.snapshot().cursor).toBe(5)

    await scheduler.pollOnce()
    expect(scheduler.snapshot().state).toBe('live')
    expect(scheduler.snapshot().failures).toBe(0)
    scheduler.stop()
  })

  it('补读抛错时不算已追平（不签 live）', async () => {
    const client = fakeClient([page([], 3, true), page([event(4)], 4)])
    const scheduler = new EventScheduler(client, { isHidden: () => false })
    let calls = 0
    scheduler.subscribe({
      name: 'tasks',
      matches: () => true,
      reload: async () => { calls += 1; if (calls === 2) throw new Error('reload failed') }
    })
    await scheduler.pollOnce()
    expect(await scheduler.pollOnce()).toBe(false)
    expect(scheduler.snapshot().state).toBe('stale')
    // 补读失败时游标停在锚点，下一轮会重做这段
    expect(scheduler.snapshot().cursor).toBe(3)
    scheduler.stop()
  })

  it('只重读受影响资源，无关事件不触发读取', async () => {
    const client = fakeClient([
      page([], 0, true),
      page([event(1, 'configuration.saved')], 1)
    ])
    const scheduler = new EventScheduler(client, { isHidden: () => false })
    let tasks = 0
    let approvals = 0
    scheduler.subscribe({ name: 'tasks', matches: (e) => e.type === 'task.updated', reload: async () => { tasks += 1 } })
    scheduler.subscribe({ name: 'approvals', matches: (e) => e.type === 'approval.decided', reload: async () => { approvals += 1 } })
    await scheduler.pollOnce()
    const baselineTasks = tasks
    const baselineApprovals = approvals
    await scheduler.pollOnce()
    expect(tasks).toBe(baselineTasks)
    expect(approvals).toBe(baselineApprovals)
    scheduler.stop()
  })

  it('后台标签页不轮询，但显式 kick 仍补读', async () => {
    const client = fakeClient([page([], 0, true), page([], 0, true)])
    let hidden = true
    const scheduler = new EventScheduler(client, { isHidden: () => hidden })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })

    expect(await scheduler.pollOnce()).toBe(false)
    expect(client.calls).toHaveLength(0)

    hidden = false
    scheduler.kick()
    expect(await scheduler.pollOnce()).toBe(true)
    expect(client.calls).toHaveLength(1)
    scheduler.stop()
  })

  it('stop 丢弃游标：新身份不会复用旧锚点', async () => {
    const client = fakeClient([page([], 9, true)])
    const scheduler = new EventScheduler(client, { isHidden: () => false })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    await scheduler.pollOnce()
    expect(scheduler.snapshot().cursor).toBe(9)
    scheduler.stop()
    expect(scheduler.snapshot().cursor).toBe(0)
    expect(scheduler.snapshot().state).toBe('idle')
  })

  it('一个调度器服务多个订阅者（共享，不是每组件一个定时器）', async () => {
    const client = fakeClient([page([], 0, true), page([event(1)], 1)])
    const scheduler = new EventScheduler(client, { isHidden: () => false })
    const a: string[] = []
    const b: string[] = []
    const offA = scheduler.subscribe({ name: 'a', matches: () => true, reload: async () => { a.push('x') } })
    const offB = scheduler.subscribe({ name: 'b', matches: () => true, reload: async () => { b.push('x') } })
    await scheduler.pollOnce()
    await scheduler.pollOnce()
    // 一次 events 请求带动两个订阅者各重读一次
    expect(client.calls).toHaveLength(2)
    expect(a).toHaveLength(2)
    expect(b).toHaveLength(2)
    offA()
    offB()
  })
})

describe('EventScheduler 复验项（R2/R3）', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  it('隐藏时继续续排：切回可见后不需要重新挂载就恢复轮询', async () => {
    const client = fakeClient([page([], 1), page([], 2), page([], 3), page([], 4)])
    let hidden = true
    const scheduler = new EventScheduler(client, {
      intervalMs: 100, hiddenIntervalMs: 200, isHidden: () => hidden
    })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    scheduler.start()

    // 首个 timer 落在隐藏状态：不发请求，但**必须续排**。
    await vi.advanceTimersByTimeAsync(100)
    expect(client.calls).toHaveLength(0)

    // 隐藏期间按降频继续跑：600ms 内应有 2 个隐藏间隔的 tick（都跳过请求）。
    await vi.advanceTimersByTimeAsync(600)
    expect(client.calls).toHaveLength(0)

    // 切回可见：表还活着，下一个 tick 就发得出请求（旧实现到这里永远停表）。
    hidden = false
    await vi.advanceTimersByTimeAsync(400)
    expect(client.calls.length).toBeGreaterThan(0)
    scheduler.stop()
  })

  it('visibilitychange 立刻把表叫回来：不靠重新挂载，也不靠写操作 kick', async () => {
    const client = fakeClient([page([], 1), page([], 2)])
    let hidden = true
    const scheduler = new EventScheduler(client, {
      intervalMs: 1000, hiddenIntervalMs: 1000, isHidden: () => hidden
    })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    scheduler.start()

    await vi.advanceTimersByTimeAsync(1000)
    expect(client.calls).toHaveLength(0)

    hidden = false
    // 监听器挂在 document 上（与浏览器一致）；只靠这个事件恢复。
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(5)
    expect(client.calls.length).toBeGreaterThan(0)
    scheduler.stop()
  })

  it('stop 之后既没有定时器也没有可见性唤醒', async () => {
    const client = fakeClient([page([], 1), page([], 2)])
    const scheduler = new EventScheduler(client, { intervalMs: 100, isHidden: () => true })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    scheduler.start()
    scheduler.stop()

    await vi.advanceTimersByTimeAsync(10_000)
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(10_000)
    expect(client.calls).toHaveLength(0)
    expect(scheduler.snapshot().state).toBe('idle')
  })

  it('资源补读失败：游标不动、记下失败资源、不签已追平，恢复后才追平', async () => {
    const client = fakeClient([page([], 5, true), page([event(6)], 6), page([], 6)])
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 100 })
    let attempts = 0
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    scheduler.subscribe({
      name: 'approvals',
      matches: () => true,
      // 初次 resync 那轮成功；第二轮（真正的事件驱动补读）失败一次。
      reload: async () => { attempts += 1; if (attempts === 2) throw new Error('read failed') }
    })

    expect(await scheduler.pollOnce()).toBe(true)
    expect(scheduler.snapshot().cursor).toBe(5)

    expect(await scheduler.pollOnce()).toBe(false)
    const snapshot = scheduler.snapshot()
    expect(snapshot.state).toBe('stale')
    // 服务端已确认到 6，我们却没读到：游标必须停在 5，不能推进。
    expect(snapshot.cursor).toBe(5)
    expect(snapshot.failedResource).toBe('approvals')

    expect(await scheduler.pollOnce()).toBe(true)
    expect(scheduler.snapshot().cursor).toBe(6)
    expect(scheduler.snapshot().failedResource).toBeNull()
    scheduler.stop()
  })

  it('resync 时任一资源失败都不推进锚点游标', async () => {
    const client = fakeClient([page([], 3, true), page([], 9, true), page([], 9, true)])
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 100 })
    let failing = false
    scheduler.subscribe({
      name: 'tasks',
      matches: () => true,
      reload: async () => { if (failing) throw new Error('503') }
    })
    await scheduler.pollOnce()
    expect(scheduler.snapshot().cursor).toBe(3)

    failing = true
    expect(await scheduler.pollOnce()).toBe(false)
    expect(scheduler.snapshot().cursor).toBe(3)
    expect(scheduler.snapshot().state).toBe('stale')

    failing = false
    expect(await scheduler.pollOnce()).toBe(true)
    expect(scheduler.snapshot().cursor).toBe(9)
    scheduler.stop()
  })

  it('退避节奏：失败后依次 1000/2000/4000/8000，并在上限封顶', async () => {
    // 时间线（intervalMs=1000，maxBackoffMs=8000）：
    //   t=1000 成功；t=2000 失败1 →1000；t=3000 失败2 →2000；t=5000 失败3 →4000；
    //   t=9000 失败4 →8000；t=17000 失败5 →16000 但被上限压到 8000；t=25000 成功。
    const client = fakeClient([
      page([], 1),
      new Error('boom'), new Error('boom'), new Error('boom'), new Error('boom'), new Error('boom'),
      page([], 1)
    ])
    const scheduler = new EventScheduler(client, {
      intervalMs: 1000, hiddenIntervalMs: 1000, maxBackoffMs: 8000, isHidden: () => false
    })
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => undefined })
    scheduler.start()

    await vi.advanceTimersByTimeAsync(1000)
    expect(client.calls).toHaveLength(1)
    expect(scheduler.snapshot().cursor).toBe(1)

    await vi.advanceTimersByTimeAsync(1000)
    expect(client.calls).toHaveLength(2)   // 第 1 次失败：一个间隔就重试
    await vi.advanceTimersByTimeAsync(1000)
    expect(client.calls).toHaveLength(3)   // 第 2 次失败：再等 1000 → 累计 2000
    await vi.advanceTimersByTimeAsync(1000)
    expect(client.calls).toHaveLength(3)   // 还没到第 3 次的 4000ms
    await vi.advanceTimersByTimeAsync(1000)
    expect(client.calls).toHaveLength(4)
    await vi.advanceTimersByTimeAsync(2000)
    expect(client.calls).toHaveLength(4)   // 第 4 次要等 8000ms
    await vi.advanceTimersByTimeAsync(2000)
    expect(client.calls).toHaveLength(5)
    await vi.advanceTimersByTimeAsync(4000)
    expect(client.calls).toHaveLength(5)
    await vi.advanceTimersByTimeAsync(4000)
    expect(client.calls).toHaveLength(6)   // t=17000，最后一次失败
    // 翻倍本应到 16000ms，被 maxBackoffMs 压成 8000ms：t=25000 就恢复。
    await vi.advanceTimersByTimeAsync(8000)
    expect(client.calls).toHaveLength(7)
    expect(scheduler.snapshot().state).toBe('live')
    expect(scheduler.snapshot().failures).toBe(0)
    expect(scheduler.snapshot().cursor).toBe(1)   // 全程没因失败推进游标
    scheduler.stop()
  })
  it('停止后作废在途轮次：迟到的响应不得重读资源或推动游标（L1）', async () => {
    let release!: (value: ZbaEventPage) => void
    const entered = { done: false }
    const client = {
      baseUrl: '/zba/v1',
      identityLabel: 'x',
      listEvents: () => new Promise<ZbaEventPage>((resolve) => {
        entered.done = true
        release = resolve
      })
    } as unknown as ZbaClient
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 100 })
    let reloads = 0
    const detach = scheduler.subscribe({
      name: 'tasks', matches: () => true, reload: async () => { reloads += 1 }
    })
    const cycle = scheduler.pollOnce()
    await Promise.resolve()
    expect(entered.done).toBe(true)
    // 最后一个引用释放：调度器停表、游标归零。
    scheduler.stop()
    release(page([], 31, true))
    expect(await cycle).toBe(false)
    const snapshot = scheduler.snapshot()
    expect(reloads).toBe(0)
    expect(snapshot.cursor).toBe(0)
    expect(snapshot.state).toBe('idle')
    detach()
  })

  it('停止后迟到的 events 拒绝不得把 idle 重新标成 stale（L1 迟到失败分支）', async () => {
    let reject!: (error: Error) => void
    const response = new Promise<ZbaEventPage>((_, no) => { reject = no })
    const client = {
      baseUrl: '/zba/v1',
      identityLabel: 'x',
      listEvents: () => response
    } as unknown as ZbaClient
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 60000 })
    let reloads = 0
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => { reloads += 1 } })
    const observed: string[] = []
    scheduler.onSnapshot((snapshot) => { observed.push(snapshot.state) })
    const cycle = scheduler.pollOnce()
    await Promise.resolve()
    // 最后一个引用释放：调度器回到 idle，游标归零。
    scheduler.stop()
    const stopped = scheduler.snapshot()
    expect(stopped.state).toBe('idle')
    // 此时迟到的传输失败才落地：失败分支同样要复核代数。
    reject(new Error('synthetic transport failure'))
    expect(await cycle).toBe(false)
    const late = scheduler.snapshot()
    expect(late.state).toBe('idle')
    expect(late.failures).toBe(0)
    expect(late.cursor).toBe(0)
    expect(reloads).toBe(0)
    // 停止后不得再向旧 listener 通知 stale。
    expect(observed).not.toContain('stale')
  })

  it('停止后连剩下的资源都不再读（L2）', async () => {
    let release!: (value: ZbaEventPage) => void
    let secondReads = 0
    const client = {
      baseUrl: '/zba/v1',
      identityLabel: 'x',
      listEvents: () => new Promise<ZbaEventPage>((resolve) => { release = resolve })
    } as unknown as ZbaClient
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 100 })
    scheduler.subscribe({ name: 'a-first', matches: () => true, reload: async () => undefined })
    scheduler.subscribe({
      name: 'b-second',
      matches: () => true,
      reload: async () => { secondReads += 1 }
    })
    const cycle = scheduler.pollOnce()
    await Promise.resolve()
    scheduler.stop()
    release(page([], 7, true))
    await cycle
    expect(secondReads).toBe(0)
  })

  it('同实例 restart 后旧在途轮次不得吃掉新代次的定时链（LIFE-01）', async () => {
    let release!: (value: ZbaEventPage) => void
    let requests = 0
    const client = {
      baseUrl: '/zba/v1',
      identityLabel: 'x',
      listEvents: () => {
        requests += 1
        return new Promise<ZbaEventPage>((resolve) => { release = resolve })
      }
    } as unknown as ZbaClient
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 50 })
    let reloads = 0
    const subscriber = { name: 'tasks', matches: () => true, reload: async () => { reloads += 1 } }
    let off = scheduler.subscribe(subscriber)
    const old = scheduler.pollOnce()
    await Promise.resolve()
    expect(requests).toBe(1)
    // 同一个实例 stop → start：新代次的定时链在旧请求还挂着的时候就排上了。
    scheduler.stop()
    scheduler.start()
    off()
    off = scheduler.subscribe(subscriber)
    // 新代次的第一条 timer 触发时，旧轮次仍然占着 ticking 互斥 → 它早退且不续排。
    await vi.advanceTimersByTimeAsync(50)
    expect(requests).toBe(1)

    release(page([], 99, true))
    expect(await old).toBe(false)
    // 旧轮次让位之后，新代次必须自己重新排上定时链，否则从此永久停表。
    // 只断言公开可见的行为：再过两个间隔应当重新发出 events 请求。
    await vi.advanceTimersByTimeAsync(120)
    expect(requests).toBeGreaterThanOrEqual(2)
    off()
    scheduler.stop()
  })

  it.each([
    { rejectOld: false, consumeTimer: false },
    { rejectOld: true, consumeTimer: false },
    { rejectOld: false, consumeTimer: true },
    { rejectOld: true, consumeTimer: true }
  ])('restart 的 kick 不被旧轮次延迟（LIFE-05：$rejectOld / $consumeTimer）', async ({ rejectOld, consumeTimer }) => {
    let resolve!: (value: ZbaEventPage) => void
    let reject!: (error: Error) => void
    const oldResponse = new Promise<ZbaEventPage>((yes, no) => { resolve = yes; reject = no })
    let requests = 0
    let inFlight = 0
    let maxInFlight = 0
    const client = fakeClient([])
    client.listEvents = async (after) => {
      requests += 1
      inFlight += 1
      maxInFlight = Math.max(maxInFlight, inFlight)
      try {
        return requests === 1 ? await oldResponse : page([], after + 1)
      } finally {
        inFlight -= 1
      }
    }
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 5000 })
    let reloads = 0
    scheduler.subscribe({ name: 'tasks', matches: () => true, reload: async () => { reloads += 1 } })
    try {
      const old = scheduler.pollOnce()
      scheduler.stop()
      scheduler.start()
      scheduler.kick()
      // 覆盖后继 timer 尚在等待、以及被在途互斥消费掉这两种顺序。
      if (consumeTimer) await vi.advanceTimersByTimeAsync(5000)
      expect(requests).toBe(1)
      if (rejectOld) reject(new Error('synthetic obsolete failure'))
      else resolve(page([], 99, true))
      expect(await old).toBe(false)
      expect(scheduler.snapshot()).toMatchObject({ cursor: 0, state: 'idle', failures: 0 })
      expect(reloads).toBe(0)
      expect(vi.getTimerCount()).toBe(1)
      // 不推进正常的 5s 间隔，kick 就必须发出下一次请求。
      await vi.advanceTimersByTimeAsync(1)
      expect(requests).toBeGreaterThan(1)
      expect(reloads).toBe(1)
      expect(maxInFlight).toBe(1)
      expect(vi.getTimerCount()).toBe(1)
    } finally {
      scheduler.stop()
    }
    expect(vi.getTimerCount()).toBe(0)
  })

  it('已 detach 的订阅者不得被这一轮的捕获列表继续执行（LIFE-02）', async () => {
    // 真实时序：捕获列表在 events 落地那一刻同步生成，此时订阅者还在；
    // 之后**前一个资源还在读**的这段时间里页面卸载 → detach。
    // 捕获列表是快照，不再核对注册归属的话，循环会照旧执行已移除的 approvals 回调。
    let release!: (value: ZbaEventPage) => void
    let releaseHost!: () => void
    let approvalsReads = 0
    const hostGate = new Promise<void>((resolve) => { releaseHost = resolve })
    const client = {
      baseUrl: '/zba/v1',
      identityLabel: 'x',
      listEvents: () => new Promise<ZbaEventPage>((resolve) => { release = resolve })
    } as unknown as ZbaClient
    const scheduler = new EventScheduler(client, { isHidden: () => false, intervalMs: 60000 })
    // 宿主自己留着一个订阅，调度器因此继续 running（不是整体释放）。
    const offHost = scheduler.subscribe({ name: 'host', matches: () => true, reload: () => hostGate })
    const offApprovals = scheduler.subscribe({
      name: 'approvals',
      matches: () => true,
      reload: async () => { approvalsReads += 1 }
    })
    const cycle = scheduler.pollOnce()
    await Promise.resolve()
    release(page([], 11, true))
    await Promise.resolve()
    // 此刻捕获列表已经生成，循环正卡在 host 的读取里；页面在这时真正卸载。
    offApprovals()
    expect(scheduler.subscriberCountNamed('approvals')).toBe(0)
    releaseHost()
    await cycle
    // 已 detach 的订阅者一次都不该被读。
    expect(approvalsReads).toBe(0)
    offHost()
    scheduler.stop()
  })
})