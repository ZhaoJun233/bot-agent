/**
 * 共享事件调度器（阶段计划 §9.3）。
 *
 * 每条规则都对应验收报告里的一项要求：
 * 1. **一个调度器，不是每组件一个定时器**：同一 client 上的所有订阅者共用一个
 *    setTimeout 链；最后一个订阅者离开时停表并丢弃游标。
 * 2. **事件只是失效通知**：事件不带结果，收到后重读资源，最终状态以资源读取为准。
 * 3. **按 sequence 去重，追平才推进游标**：只处理 sequence > cursor 的事件；
 *    整页处理成功后游标推进到服务端确认的 nextSequence（分页追完才推进）。
 * 4. **初次同步 / resyncRequired**：先记下该响应的锚点，整份重读所有订阅资源，
 *    全部成功后才把游标设到锚点。重读期间产生的事件仍从锚点补读，不丢事件。
 * 5. **补读失败保持旧数据**：任一订阅者抛错就不推进游标、不签"已追平"，标记 stale
 *    并指数退避。页面必须把读取失败**抛给调度器**，不能自己吞掉（R2）。
 * 6. **后台标签页降频而不是停表**：隐藏时跳过请求但**继续续排**，并且监听
 *    visibilitychange，切回可见时立刻补一轮——不要求重新挂载或写操作 kick（R3）。
 * 7. **认证/基址变更**：release() 丢弃游标与快照，旧身份的游标绝不复用。
 * 8. **停止后作废在途轮次**：last release 之后，那个已经发出的 events 请求迟到返回时
 *    不能再重读资源、不能把游标/状态写回去——成功与失败两条路径都要复核会话代数；迟到的 reject 也不得把
 *    idle 的实例重新标成 stale、累加 failures 或通知旧 listener。停表不等于停轮（L1）。
 *
 * 首次 tick 安排在一个间隔之后：挂载时页面已经整份读取过，立刻再取一次只是
 * 重复流量。
 */
import type { ZbaClient, ZbaEvent } from '../../api/zba'

export type SyncState = 'idle' | 'live' | 'syncing' | 'stale'

export interface SyncSubscriber {
  /** 资源名，只用于调试与测试断言。 */
  name: string
  /** 这条事件是否影响本资源。 */
  matches(event: ZbaEvent): boolean
  /** 重读本资源；抛错表示补读失败（保持旧数据，不签已追平）。 */
  reload(): Promise<void>
}

export interface SchedulerSnapshot {
  state: SyncState
  cursor: number
  resyncCount: number
  failures: number
  lastSyncedAt: string | null
  /**
   * 最近一次失败发生在哪一类读取上。
   *
   * null 且 state==='stale'：events 请求本身失败（游标可能仍是新的）。
   * 非 null：events 读到了，但某个资源的补读失败——旧数据可能已经过期，
   * 而服务端已经确认到更晚的位置（R2）。界面必须把这两句话分开说。
   */
  failedResource: string | null
}

export interface SchedulerOptions {
  /** 前台轮询间隔。 */
  intervalMs?: number
  /** 后台（标签页不可见）间隔。 */
  hiddenIntervalMs?: number
  /** 失败退避上限。 */
  maxBackoffMs?: number
  /** 可见性探测，测试注入用。 */
  isHidden?: () => boolean
  now?: () => Date
}

/**
 * 轮询基线（2026-10-23 实测，合成宿主，见 .git/s3-fix-20261023-1200/polling-baseline/）。
 *
 * 稳态空页 events 请求：**102 字节**、p50 0.49ms、p95 0.81ms、max 5.96ms（300 次采样）。
 * 整份重读任务列表对照：857 字节 / p50 4.88ms。
 *
 * 换算成本：
 * - 前台 5s：720 次/时、约 71.7 KiB/时（≈ 一条任务重读的 12%）
 * - 后台 30s：120 次/时、约 12 KiB/时
 * - 退避上限 60s：60 次/时、约 6 KiB/时
 *
 * 结论：事件轮询比"整份重读"便宜一个数量级，5s 前台间隔的绝对成本可以忽略；
 * 真正贵的是事件触发的资源重读，所以调度器宁可多轮询、也要保证「只重读受影响的资源」。
 * 这几个数字只用于容量核算，不参与任何"是否实现"的判断。
 */
const DEFAULTS = { intervalMs: 5000, hiddenIntervalMs: 30000, maxBackoffMs: 60000 }
const PAGE_SIZE = 100

export class EventScheduler {
  private readonly client: ZbaClient
  private readonly intervalMs: number
  private readonly hiddenIntervalMs: number
  private readonly maxBackoffMs: number
  private readonly isHidden: () => boolean
  private readonly now: () => Date

  private readonly subscribers = new Set<SyncSubscriber>()
  private readonly listeners = new Set<(snapshot: SchedulerSnapshot) => void>()
  private timer: ReturnType<typeof setTimeout> | null = null
  private visibilityHandler: (() => void) | null = null
  private running = false
  private ticking = false
  private initialized = false
  private cursor = 0
  private resyncCount = 0
  private consecutiveResync = 0
  private failures = 0
  private state: SyncState = 'idle'
  private lastSyncedAt: string | null = null
  private failedResource: string | null = null
  /**
   * 会话代数：stop() 时 +1，作废所有在途轮次与已捕获的订阅者列表。
   *
   * 定时器可以取消，但已经 await 的网络请求不能；没有这道闸，旧身份的补读会在
   * 停止之后把资源写回、把游标推回去（L1/L2）。
   */
  private epoch = 0
  /** 本地写过之后要尽快补读一次；只影响节奏，不影响游标语义。 */
  private kickRequested = false

  constructor(client: ZbaClient, options: SchedulerOptions = {}) {
    this.client = client
    this.intervalMs = positive(options.intervalMs, DEFAULTS.intervalMs)
    this.hiddenIntervalMs = positive(options.hiddenIntervalMs, DEFAULTS.hiddenIntervalMs)
    this.maxBackoffMs = positive(options.maxBackoffMs, DEFAULTS.maxBackoffMs)
    this.isHidden = options.isHidden ?? defaultHidden
    this.now = options.now ?? (() => new Date())
  }

  /**
   * 当前注册中的同名订阅者数量。
   *
   * 页面自己接管某类资源时（配置页/能力页挂载），宿主级探测可以让位，避免同一次
   * 事件触发两次相同资源的读取。
   */
  subscriberCountNamed(name: string): number {
    let count = 0
    for (const subscriber of this.subscribers) if (subscriber.name === name) count += 1
    return count
  }

  subscribe(subscriber: SyncSubscriber): () => void {
    this.subscribers.add(subscriber)
    if (!this.running) this.start()
    return () => {
      this.subscribers.delete(subscriber)
      if (this.subscribers.size === 0) this.stop()
    }
  }

  onSnapshot(listener: (snapshot: SchedulerSnapshot) => void): () => void {
    this.listeners.add(listener)
    listener(this.snapshot())
    return () => { this.listeners.delete(listener) }
  }

  snapshot(): SchedulerSnapshot {
    return {
      state: this.state,
      cursor: this.cursor,
      resyncCount: this.resyncCount,
      failures: this.failures,
      lastSyncedAt: this.lastSyncedAt,
      failedResource: this.failedResource
    }
  }

  start(): void {
    if (this.running) return
    this.running = true
    this.watchVisibility()
    this.schedule(this.intervalMs)
  }

  stop(): void {
    this.running = false
    // 在途轮次就此作废：它们醒来后不得再读写任何状态。
    this.epoch += 1
    this.clearTimer()
    this.unwatchVisibility()
    // 游标属于这一次身份/基址；释放后绝不能带给下一个会话。
    this.cursor = 0
    this.failedResource = null
    this.initialized = false
    this.failures = 0
    this.resyncCount = 0
    this.consecutiveResync = 0
    this.kickRequested = false
    this.state = 'idle'
    this.emit()
  }

  /** 本地写完（提交/取消/决定）后尽快补读一次。 */
  kick(): void {
    if (!this.running) return
    this.kickRequested = true
    if (this.ticking) return
    this.clearTimer()
    this.schedule(0)
  }

  /**
   * 走一轮。公开是为了让测试确定性驱动，不必等真实定时器。
   * 返回 true 表示这一轮成功（含"确认没有新事件"）。
   */
  async pollOnce(): Promise<boolean> {
    if (!this.running || this.ticking) return false
    if (this.subscribers.size === 0) return false
    // 后台标签页不发请求，但**必须续排**：否则这一个 timer 被消费之后就再也不会
    // 有人轮询（R3）。切回前台时 visibilitychange 会立刻补一轮，不等降频间隔。
    if (this.isHidden() && !this.kickRequested) {
      this.schedule(this.hiddenIntervalMs)
      return false
    }

    this.ticking = true
    this.kickRequested = false
    // 这一轮的归属：每次 await 之后都要重新对一遍，停止/换会话立刻失效。
    const epoch = this.epoch
    const current = (): boolean => this.running && epoch === this.epoch
    try {
      const page = await this.client.listEvents(this.cursor, PAGE_SIZE)
      if (!current()) return false
      const anchor = page.nextSequence

      if (page.resyncRequired || !this.initialized) {
        // 有缺口或初次同步：先记住锚点，整份重读，全部成功后才推进游标。
        const resync = page.resyncRequired
        if (resync) {
          this.resyncCount += 1
          this.consecutiveResync += 1
        } else {
          this.consecutiveResync = 0
        }
        this.setState('syncing')
        // 任一资源补读失败就保持旧游标 + stale：不能签"已追平"（R2）。
        const failedDuringResync = await this.runReloads([...this.subscribers], current)
        if (!current()) return false
        if (failedDuringResync !== null) {
          this.fail(failedDuringResync)
          this.schedule(this.backoffDelay())
          return false
        }
        this.cursor = Math.max(this.cursor, anchor)
        this.initialized = true
        this.settle()
        // 重读期间可能又产生事件：立刻从锚点补读一轮，不丢事件。
        // 连续 resync 说明服务端在反复要求重读，必须退避，不能忙循环。
        this.schedule(this.consecutiveResync >= 2 ? this.backoffDelay() : 0)
        return true
      }

      this.consecutiveResync = 0

      const fresh = page.items
        .filter((event) => event.sequence > this.cursor)
        .sort((left, right) => left.sequence - right.sequence)

      const affected = new Set<SyncSubscriber>()
      for (const event of fresh) {
        for (const subscriber of this.subscribers) {
          if (subscriber.matches(event)) affected.add(subscriber)
        }
      }

      if (affected.size > 0) {
        this.setState('syncing')
        const failed = await this.runReloads([...affected], current)
        if (!current()) return false
        if (failed !== null) {
          // 事件已读到但资源补读失败：游标不推进，下一轮从旧游标重拿这段事件。
          this.fail(failed)
          this.schedule(this.backoffDelay())
          return false
        }
      }

      // 整页处理成功才推进游标：anchor 就是服务端确认的已追平位置。
      this.cursor = Math.max(this.cursor, anchor)
      this.settle()
      // 这一页满了说明还有积压，追完再等间隔。
      this.schedule(page.items.length >= PAGE_SIZE ? 0 : this.nextIntervalMs())
      return true
    } catch {
      // events 请求本身失败：不知道服务端追平到哪里，游标原样保留。
      //
      // 成功路径在 await 之后都复核 current()，失败路径同样必须复核（L1 的迟到
      // reject）：last release 之后才落地的拒绝不得把已经 idle 的实例重新标成
      // stale、不得累加 failures、不得通知旧 listener，也不得重排定时器。
      if (!current()) return false
      this.failures += 1
      this.failedResource = null
      this.setState('stale')
      this.schedule(this.backoffDelay())
      return false
    } finally {
      this.ticking = false
      // 这一轮被后继代次取代（stop/start 或末订阅 off 后立刻 resubscribe）时，新代次的
      // 定时器可能已经在旧请求还没返回时触发过一次：ticking 互斥让它直接早退且不续排，
      // 那一条 timer 就被消费掉了。补排的责任因此落在这条出口上——否则新代次从此永久
      // 停表，只有额外的 kick/visibilitychange 才能偶然救回来（LIFE-01）。
      // 新代次有 kick 时，即使正常 timer 还在等待，也必须用立即补排替换它（LIFE-05）。
      // 没有 kick 则只在 timer 被消费后续排；schedule 会清掉旧 timer，始终只留一条链。
      if (epoch !== this.epoch && this.running && (this.kickRequested || this.timer === null)) {
        this.schedule(this.kickRequested ? 0 : this.nextIntervalMs())
      }
    }
  }

  /** 前台/后台各自的正常间隔。 */
  private nextIntervalMs(): number {
    return this.isHidden() ? this.hiddenIntervalMs : this.intervalMs
  }

  /**
   * 退避节奏：第 1 次失败按正常间隔重试，之后每次翻倍，封顶 maxBackoffMs。
   *
   * 从 2^0 而不是 2^1 起算：一次抖动不该让补读推迟两个间隔那么久
   * （5s → 5s → 10s → 20s…），也满足「重复 resync 要退避，不能忙循环」。
   */
  private backoffDelay(): number {
    const base = this.nextIntervalMs()
    const steps = Math.max(0, Math.min(this.failures - 1, 5))
    return Math.min(base * 2 ** steps, this.maxBackoffMs)
  }

  /**
   * 可见性变化：切回可见时立刻补一轮（不等降频 timer），切到后台时保持续排。
   * 只有 visibilitychange 能做到"不重新挂载、不靠写操作 kick 也能恢复"（R3）。
   */
  private watchVisibility(): void {
    if (this.visibilityHandler !== null) return
    const doc = documentFor()
    if (!doc || typeof doc.addEventListener !== 'function') return
    this.visibilityHandler = () => {
      if (doc.visibilityState === 'hidden') return
      this.kick()
    }
    doc.addEventListener('visibilitychange', this.visibilityHandler)
  }

  private unwatchVisibility(): void {
    if (this.visibilityHandler === null) return
    const doc = documentFor()
    if (doc && typeof doc.removeEventListener === 'function') {
      doc.removeEventListener('visibilitychange', this.visibilityHandler)
    }
    this.visibilityHandler = null
  }

  /**
   * 顺序重读一批资源，返回第一个失败的资源名（全部成功返回 null）。
   *
   * 顺序执行：并发重读会让"哪一份是最终状态"变得不可判定。
   * 单个资源失败不影响其它资源的独立重读，但调用方必须据此拒绝签"已追平"。
   *
   * 每读一个资源之前都确认这一轮还属于当前会话：停止之后连剩下的资源都不该再读
   * （L2 的"停止后仍继续 1 次旧能力读取"就是这么来的）。
   */
  private async runReloads(
    subscribers: SyncSubscriber[],
    current: () => boolean
  ): Promise<string | null> {
    let firstFailure: string | null = null
    for (const subscriber of subscribers) {
      if (!current()) return firstFailure
      // 捕获的列表是"这一轮开始时"的快照：执行前再核一次注册归属。宿主调度器还活着、
      // 但订阅者中途 detach（任务页真正卸载）时，它已经不再代表任何界面，替它重读只会
      // 写回孤儿 refs 并多发一次同身份只读（LIFE-02）。
      if (!this.subscribers.has(subscriber)) continue
      try {
        await subscriber.reload()
      } catch {
        if (firstFailure === null) firstFailure = subscriber.name
      }
    }
    return firstFailure
  }

  /** 资源补读失败：记 stale、留住游标、按退避重来。 */
  private fail(resource: string): void {
    this.failures += 1
    this.failedResource = resource
    this.setState('stale')
  }

  private settle(): void {
    this.failures = 0
    this.failedResource = null
    this.lastSyncedAt = this.now().toISOString()
    this.setState('live')
  }

  private setState(next: SyncState): void {
    if (this.state === next) return
    this.state = next
    this.emit()
  }

  private emit(): void {
    const snapshot = this.snapshot()
    for (const listener of this.listeners) listener(snapshot)
  }

  private schedule(delayMs: number): void {
    if (!this.running) return
    this.clearTimer()
    this.timer = setTimeout(() => {
      this.timer = null
      void this.pollOnce()
    }, Math.max(0, delayMs))
    // 浏览器忽略 unref；Node 下不要因为这个定时器把进程钉住。
    const handle = this.timer as unknown as { unref?: () => void }
    handle?.unref?.()
  }

  private clearTimer(): void {
    if (this.timer === null) return
    clearTimeout(this.timer)
    this.timer = null
  }
}

function positive(value: number | undefined, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : fallback
}

interface VisibilityDocument {
  visibilityState?: string
  addEventListener?: (type: 'visibilitychange', listener: () => void) => void
  removeEventListener?: (type: 'visibilitychange', listener: () => void) => void
}

function documentFor(): VisibilityDocument | undefined {
  return (globalThis as { document?: VisibilityDocument }).document
}

function defaultHidden(): boolean {
  return documentFor()?.visibilityState === 'hidden'
}

interface RegistryEntry {
  scheduler: EventScheduler
  refs: number
}

const registry = new WeakMap<ZbaClient, RegistryEntry>()

/**
 * 取同一个 client 上的共享调度器（引用计数）。
 * 最后一个使用者 release 时停表并丢弃游标——旧身份/旧基址的游标绝不带给新会话。
 */
export function acquireScheduler(
  client: ZbaClient,
  options: SchedulerOptions = {}
): { scheduler: EventScheduler; release: () => void } {
  let entry = registry.get(client)
  if (!entry) {
    entry = { scheduler: new EventScheduler(client, options), refs: 0 }
    registry.set(client, entry)
  }
  const current = entry
  current.refs += 1
  let released = false
  return {
    scheduler: current.scheduler,
    release: () => {
      if (released) return
      released = true
      current.refs -= 1
      if (current.refs > 0) return
      current.scheduler.stop()
      registry.delete(client)
    }
  }
}
