/**
 * 五类资源的共享补读（阶段计划 §9.3 第 240 行）。
 *
 * 计划要求 resync 时「重新读取 status、配置、任务、审批及受影响能力，全部成功后
 * 才能从记录的锚点继续补读事件」。任务与审批由任务页自己订阅；这个模块补上另外
 * 三类，并把结果发布成本地快照，供各页面判断「我手上的基线是不是过期了」。
 *
 * 为什么不是直接调用各页面的 reload()：
 *
 * - 页面可能没挂载（用户从没打开过配置页），也就没有 reload 可调；
 * - 配置页有草稿与待核对操作键，后台强制重读会破坏「草稿不被静默覆盖」的语义。
 *
 * 所以这里只做**只读的轻量有效性探测**：读到的版本号/代际/描述符修订发布到
 * appResourceSignals；页面自己 watch 它，按各自语义决定要不要重读、怎么处理冲突。
 * 探测本身用只读路由，不带任何写意图：
 *
 * - `/status`（Read）——就绪状态与配置版本；
 * - `/configuration/read` kind=0（Read）——隐藏投影，字段不带值，所以拿不到秘密；
 * - `/capabilities` limit=1（Read）——描述符修订，用来判断能力目录是否变化。
 *
 * 探测失败会**抛给调度器**（不是自己吞掉），调度器据此不签「已追平」（R2）。
 *
 * 写回自身也带会话代数（L2）：reset 之后，旧会话那次还没返回的读取**不得**把数据
 * 重新写进全局信号——只在调度器返回后检查归属不足以撤销已经写入的值，所以每个
 * 读取在 await 前后各对一次代数。
 */
import { reactive } from 'vue'
import type { ZbaClient } from '../../api/zba'
import type { EventScheduler, SyncSubscriber } from './event-scheduler'

export interface AppResourceSignals {
  /** 最近一次成功读到的 status 摘要；null 表示还没读到过。 */
  status: { state: string; ready: boolean; configurationVersion: string | null } | null
  /** 最近一次成功读到的配置代际与版本（不含任何字段值）。 */
  configuration: { generation: string; version: string } | null
  /** 最近一次成功读到的能力目录修订；count 是探测页（limit=1）的条数，不是目录总数。 */
  capabilities: { descriptorRevision: string; count: number } | null
  /** 各类资源最近一次读取失败的安全错误码（成功后清空）。 */
  errors: { status: string | null; configuration: string | null; capabilities: string | null }
  /** 成功读取的时刻（ISO）。 */
  updatedAt: string | null
}

/** 模块级快照：多个页面读同一份，避免各自再发一遍同样的请求。 */
export const appResourceSignals = reactive<AppResourceSignals>({
  status: null,
  configuration: null,
  capabilities: null,
  errors: { status: null, configuration: null, capabilities: null },
  updatedAt: null
})

/**
 * 会话代数：reset 时 +1。
 *
 * 全局信号是跨页面共享的，必须能证明"这次写入属于当前会话"；旧会话迟到的配置/
 * 能力摘要一旦写进去，新会话就会看到上一个身份的基线（L2）。
 */
let signalsEpoch = 0

function errorCode(cause: unknown): string {
  if (typeof cause === 'object' && cause !== null && 'code' in cause) {
    const code = (cause as { code?: unknown }).code
    if (typeof code === 'string' && code.length > 0) return code
  }
  return 'read_failed'
}

async function readStatus(client: ZbaClient): Promise<void> {
  const epoch = signalsEpoch
  try {
    const status = await client.status()
    // 旧会话的读取迟到：不写回，也不当作当前会话的结论。
    if (epoch !== signalsEpoch) return
    // 只发布形状，不发布内容。桩 client 可能返回残缺对象：字段缺失不报错，
    // 真正读不到（请求失败）才抛给调度器。
    appResourceSignals.status = {
      state: status.state,
      ready: status.ready,
      configurationVersion: status.configurationVersion ?? null
    }
    appResourceSignals.errors.status = null
    appResourceSignals.updatedAt = new Date().toISOString()
  } catch (cause) {
    if (epoch === signalsEpoch) appResourceSignals.errors.status = errorCode(cause)
    throw cause
  }
}

async function readConfiguration(client: ZbaClient): Promise<void> {
  const epoch = signalsEpoch
  try {
    // kind=0：隐藏投影。只要版本与代际，不要任何字段值。
    const result = await client.readConfiguration(0)
    if (epoch !== signalsEpoch) return
    appResourceSignals.configuration = {
      generation: typeof result?.generation === 'string' ? result.generation : '',
      version: typeof result?.version === 'string' ? result.version : ''
    }
    appResourceSignals.errors.configuration = null
    appResourceSignals.updatedAt = new Date().toISOString()
  } catch (cause) {
    if (epoch === signalsEpoch) appResourceSignals.errors.configuration = errorCode(cause)
    throw cause
  }
}

async function readCapabilities(client: ZbaClient): Promise<void> {
  const epoch = signalsEpoch
  try {
    // limit=1：只要描述符修订，不拉整份目录。
    const page = await client.listCapabilities({ limit: 1 })
    if (epoch !== signalsEpoch) return
    appResourceSignals.capabilities = {
      descriptorRevision: typeof page?.descriptorRevision === 'string' ? page.descriptorRevision : '',
      count: Array.isArray(page?.items) ? page.items.length : 0
    }
    appResourceSignals.errors.capabilities = null
    appResourceSignals.updatedAt = new Date().toISOString()
  } catch (cause) {
    if (epoch === signalsEpoch) appResourceSignals.errors.capabilities = errorCode(cause)
    throw cause
  }
}

/**
 * 这三类资源各自何时需要补读：事件按类型，resync 时全部。
 *
 * `canRead` 逐类判断而不是假设 client 完整：客户端可能只实现了任务链路（测试桩
 * 就是如此）。对**真实** ZbaClient 这三条都是函数，所以五类资源一定接齐。
 */
export function appResourceSubscribers(client: ZbaClient): SyncSubscriber[] {
  const all: Array<SyncSubscriber & { canRead: boolean }> = [
    {
      name: 'status',
      canRead: typeof client.status === 'function',
      // status 是整机投影：任何事件都可能改变它，所以任何事件都要重读它。
      matches: () => true,
      reload: () => readStatus(client)
    },
    {
      name: 'configuration',
      canRead: typeof client.readConfiguration === 'function',
      matches: (event) => event.type === 'configuration.saved',
      reload: () => readConfiguration(client)
    },
    {
      name: 'capabilities',
      canRead: typeof client.listCapabilities === 'function',
      matches: (event) => event.type === 'capability.executed',
      reload: () => readCapabilities(client)
    }
  ]
  return all.filter((subscriber) => subscriber.canRead).map(({ canRead: _canRead, ...subscriber }) => subscriber)
}

/**
 * 宿主级覆盖：让调用方（App.vue）把「status」这条资源的补读接到自己真正的读取上，
 * 而不是只更新本模块的信号量——否则概览页拿不到重读结果，接齐就是空话。
 */
export interface AppResourceOverrides {
  status?(): Promise<void>
}

interface Attachment {
  refs: number
  detach: () => void
}

const attachments = new WeakMap<EventScheduler, Attachment>()

/**
 * 把三类资源挂到共享调度器上，返回解除订阅的函数。
 *
 * 挂上之后它们和任务、审批共用同一个 events 轮询表：一次 events 请求，按事件类型
 * 分流到各自资源；resync 时五类一起补读，任一失败都不签已追平。
 *
 * 按调度器引用计数：App.vue 与任务页都会登记，同一个调度器只挂一份订阅，
 * 最后一个使用者离开时才解除（不会重复读同一资源）。
 */
export function registerAppResources(
  scheduler: EventScheduler,
  client: ZbaClient,
  overrides: AppResourceOverrides = {}
): () => void {
  let attachment = attachments.get(scheduler)
  if (!attachment) {
    const subscribers = appResourceSubscribers(client).map((subscriber) => {
      if (subscriber.name === 'status' && typeof overrides.status === 'function') {
        return { ...subscriber, reload: overrides.status }
      }
      // 页面已经挂载并自己接管这个资源时（配置页/能力页），宿主级探测让位：
      // 同一次事件不该把同一资源读两遍，而且页面版本带着各自的草稿/错误语义。
      if (subscriber.name === 'configuration' || subscriber.name === 'capabilities') {
        const name = subscriber.name
        const probe = subscriber.reload
        return { ...subscriber, reload: () => (
          scheduler.subscriberCountNamed(name) > 1 ? Promise.resolve() : probe()
        ) }
      }
      return subscriber
    })
    const offs = subscribers.map((subscriber) => scheduler.subscribe(subscriber))
    attachment = { refs: 0, detach: () => { for (const off of offs) off() } }
    attachments.set(scheduler, attachment)
  }
  const current = attachment
  current.refs += 1
  let released = false
  return () => {
    if (released) return
    released = true
    current.refs -= 1
    if (current.refs === 0) {
      current.detach()
      attachments.delete(scheduler)
    }
  }
}

/**
 * 会话/身份变更：快照属于旧身份，必须清空并作废在途读取的写回权。
 *
 * 先推进代数再清空：这样任何已经发出的读取回来时都对不上号，不会把旧身份的数据
 * 重新写进全局信号（L2）。
 */
export function resetAppResourceSignals(): void {
  signalsEpoch += 1
  appResourceSignals.status = null
  appResourceSignals.configuration = null
  appResourceSignals.capabilities = null
  appResourceSignals.errors.status = null
  appResourceSignals.errors.configuration = null
  appResourceSignals.errors.capabilities = null
  appResourceSignals.updatedAt = null
}
