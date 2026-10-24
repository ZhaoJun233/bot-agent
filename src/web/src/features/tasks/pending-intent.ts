/**
 * 未决提交的最小关联信息（阶段计划 §9.2 第 226 行、§9.3 第 242 行）。
 *
 * 锁定/断线之后，用户仍需要能**只读核对**那个已经发出去、但没拿到回执的提交。
 * 这里只保留核对必需的东西：
 *
 * - 允许：幂等键、来源 key、任务 id、核对结论、写入时刻、发起身份；
 * - 禁止：prompt、令牌、秘密、请求体、结果摘要。
 *
 * 只存**内存**，不落 localStorage/sessionStorage/URL：页面刷新即丢失
 * （刷新后按计划从授权任务列表恢复，不伪造历史）。
 *
 * 记录按「原服务标识 + 本地身份上下文标签」隔离：换了服务或换了身份就拿不到
 * 旧记录，也就不会把旧标识发给新服务。标签是令牌的**单向摘要**，只用于本机
 * 比对，绝不外发、不参与任何鉴权。
 *
 * **为什么是一个响应式的模块级登记处，而不是组件里的一个 Map**（R1）：
 * 一次提交的网络生命周期比组件长。锁定会把任务页整个卸载，而那个 fetch 还在
 * 飞行中；新会话重新挂载任务页时，如果登记处是组件私有的、非响应式的，新组件
 * 永远看不到这次在途请求——于是同一个意图可以换成第二个幂等键被服务端第二次
 * 受理。所以：
 *
 * 1. 登记发生在**发出请求之前**（beginPending），不是失败之后；
 * 2. 记录归属登记处，不归属组件：组件卸载后，发起它的那次异步流程仍然可以把
 *    结果写回同一条记录（按 token，不按组件）；
 * 3. 记录表是响应式的，任何时刻挂载的页面都能立刻读到最新状态。
 * 4. **终态也要传播**（M2）：拿到稳定 id 或明确拒绝之后，记录不是"删掉了事"，而是
 *    带着终态留在表里（TTL 内），这样重连后恢复出来的那一行才知道该怎么结束——
 *    否则它会永远停在"已提交，正在等待服务端受理…"。终态记录不再占用提交入口。
 * 5. **容量按阶段分开，且未决按身份划分**（CAPACITY-M1）：终态与未决**不共享**名额。
 *    未决记录永远不参与淘汰（满额就拒绝新登记，绝不静默丢掉仍可能已受理的关联），
 *    并且未决名额按「服务 + 身份」分别计算——这样 B 身份连续明确拒绝既不会占用、
 *    也不会耗尽 A 身份的未决名额，跨身份影响被彻底切断。终态有独立上限与 TTL，
 *    超出时只淘汰最旧的终态。
 */
import { ref } from 'vue'
import type { ZbaTaskFindCode } from '../../api/zba'

/** 这条关联现在处在哪个阶段。 */
export type PendingPhase =
  /** 请求已登记并准备发出/正在飞行中，还没有可信回执。 */
  | 'in_flight'
  /** 发出去了但没有拿到可信结论（网络中断、5xx、畸形回包…）。 */
  | 'unresolved'
  /** 已经拿到稳定任务 id（受理或核对 found），不再阻止新提交。 */
  | 'resolved'
  /** 服务端明确拒绝（创建之前回绝），不占用未决名额。 */
  | 'rejected'

export interface PendingIntent {
  /** 登记句柄：只有发起这次提交的异步流程能回写它。 */
  readonly token: number
  /** 幂等键。 */
  readonly key: string
  /** 来源 key（`panel:…`），只用于展示上下文。 */
  readonly sourceKey: string
  /** 请求是否确实已经发出（登记但未发出与已发出的措辞不同）。 */
  requestSent: boolean
  /** 已知的任务 id（受理回执或核对所得）。 */
  taskId: string | null
  /** 上次核对结论。 */
  findCode: ZbaTaskFindCode | null
  /** 关联进入当前阶段的时刻（ISO）。 */
  savedAt: string
  /** 原服务标识（origin + API 基址）。 */
  readonly service: string
  /** 本地身份上下文标签（令牌摘要，不参与鉴权）。 */
  readonly identity: string
  /** 发起这次提交的组件代数（用于同一组件内的显示关联，不做权限判断）。 */
  readonly bornOf: number
  phase: PendingPhase
}

/**
 * 每个「服务 + 身份」的未决记录（in_flight / unresolved）上限。
 *
 * 这一档**永远不淘汰**：每条未决记录都可能已经在服务端被受理，静默丢掉它等于
 * 放开同一意图的换键重提入口（CAPACITY-M1）。满额时 `beginPending` 抛
 * `PendingCapacityError`，由调用方显式阻止这次提交。
 *
 * 名额**按身份分开**：一个身份把自己 8 个未决名额占满，也只会停用它自己的提交
 * 入口，不会连带停用其它身份，更不会挤掉别人的未决关联。
 */
const ACTIVE_LIMIT = 8

/**
 * 终态记录（resolved / rejected）上限。
 *
 * 终态只用于把结论传播给重连后的恢复行，与未决容量**互不占用**；超出时先淘汰
 * 最旧的终态——它不影响"未知结果不得换键重提"这条硬约束。
 */
const SETTLED_LIMIT = 8

/**
 * 终态记录保留时长。
 *
 * 太短则锁定/重连的窗口里恢复行收不到结论；太长则只是占着内存。10 分钟足够覆盖
 * "发出请求 → 锁定 → 重新认证 → 打开任务页"这条真实路径。
 */
const SETTLED_TTL_MS = 10 * 60 * 1000

/** 未决：还没能证明这次提交到底有没有被服务端受理。 */
function isActive(item: PendingIntent): boolean {
  return item.phase === 'in_flight' || item.phase === 'unresolved'
}

/**
 * 未决记录满额。
 *
 * 这**不是**内部错误：它表示"再登记一条就要丢掉别的未决关联"，所以宁可拒绝这次
 * 提交，也不能把仍可能已受理的旧关联挤出去。
 */
export class PendingCapacityError extends Error {
  readonly active: number
  readonly limit: number

  constructor(active: number, limit: number) {
    super('pending_capacity_exceeded')
    this.name = 'PendingCapacityError'
    this.active = active
    this.limit = limit
  }
}

/**
 * 响应式记录表。
 *
 * 用 \`ref\` 包住数组并在变更时整体替换，是为了让任何组件的计算属性能在
 * "旧请求后失败"这一刻重新求值——那正是 R1 的时序。
 */
const records = ref<PendingIntent[]>([])

/**
 * 登记表的响应式句柄，供 watch 使用。
 *
 * 每次变更整体替换数组，所以浅 watch 就够；不要 deep watch（记录是只读字段为主）。
 */
export const pendingRegistry = records

let nextToken = 1

/** 只读地观察登记表（测试与界面用）；在计算属性里调用可以拿到响应式更新。 */
export function pendingEntries(): readonly PendingIntent[] {
  return records.value
}

/** 发起一次提交前先登记：此刻还没有任何回执。 */
export function beginPending(input: {
  key: string
  sourceKey: string
  service: string
  identity: string
  bornOf?: number
}): PendingIntent {
  const record: PendingIntent = {
    token: nextToken++,
    key: input.key,
    sourceKey: input.sourceKey,
    requestSent: false,
    taskId: null,
    findCode: null,
    savedAt: new Date().toISOString(),
    service: input.service,
    identity: input.identity,
    bornOf: input.bornOf ?? 0,
    phase: 'in_flight'
  }
  // 同键只保留一条：重复登记（理论上不该发生）不制造第二个未决名额。
  const rest = records.value.filter((item) => item.key !== record.key)
  // 容量只由**本服务 + 本身份的未决**记录占用：终态记录再多也不会挤掉这次登记，
  // 别的身份占满也不会牵连这里。CAPACITY-M1 的反例正是全局混排造成的。
  const active = rest.filter((item) => isActive(item)
    && item.service === record.service
    && item.identity === record.identity).length
  if (active >= ACTIVE_LIMIT) throw new PendingCapacityError(active, ACTIVE_LIMIT)
  records.value = enforceCapacity([record, ...rest])
  return record
}

/**
 * 容量裁剪：未决记录一律保留，终态记录按独立上限从最旧的开始淘汰。
 *
 * 只有终态参与淘汰：任何未决关联（含别的身份的）都不会因为容量被丢掉。
 * 顺序保持"最新在前"不变——回写只改内容、不改位置，界面行序与恢复顺序都依赖它。
 */
function enforceCapacity(next: PendingIntent[]): PendingIntent[] {
  const settled = next.filter((item) => !isActive(item)).length
  let excess = settled - SETTLED_LIMIT
  if (excess <= 0) return next
  const kept: PendingIntent[] = []
  // 从最旧的一端开始丢，且只丢终态；未决记录不参与淘汰。
  for (const item of [...next].reverse()) {
    if (excess > 0 && !isActive(item)) {
      excess -= 1
      continue
    }
    kept.push(item)
  }
  return kept.reverse()
}

function replace(next: PendingIntent[]): void {
  records.value = next
}

/** 请求确实已经离开浏览器（此后失败一律不能声称"没有产生任务"）。 */
export function markPendingSent(token: number | null | undefined): void {
  if (token === null || token === undefined) return
  replace(records.value.map((item) => (item.token === token
    ? { ...item, requestSent: true, savedAt: new Date().toISOString() }
    : item)))
}

/**
 * 拿到稳定任务 id / 明确拒绝 / 用户显式放弃：不再阻止新一次提交。
 *
 * 三种终态都**保留记录**（区别只在 `phase`）：重连后恢复出来的那一行靠它知道
 * "这次提交最后是什么结论"。占用入口与否由 `activePending` 决定，不影响提交。
 */
export function settlePending(
  token: number | null | undefined,
  phase: 'resolved' | 'rejected',
  taskId?: string | null
): void {
  if (token === null || token === undefined) return
  const target = records.value.find((item) => item.token === token)
  if (!target) return
  replace(records.value.map((item) => (item.token === token
    ? {
        ...item,
        phase,
        taskId: taskId === undefined ? item.taskId : taskId,
        savedAt: new Date().toISOString()
      }
    : item)))
  pruneSettled()
}

/**
 * 用户显式放弃：这条关联不再需要核对，也不再需要显示结论。
 *
 * 与 settle 的区别在于语义：`settlePending` 是"这件事有结论了，结论要留着传播"，
 * `discardPending` 是"我不需要这条记录了"。
 */
export function discardPending(key: string): void {
  forgetPending(key)
}

/** 终态记录按 TTL 与独立上限淘汰，避免长期占位；未决记录一条都不动。 */
function pruneSettled(): void {
  const cutoff = Date.now() - SETTLED_TTL_MS
  const kept = records.value.filter((item) => {
    if (isActive(item)) return true
    const at = Date.parse(item.savedAt)
    return Number.isNaN(at) ? true : at >= cutoff
  })
  replace(enforceCapacity(kept))
}

/**
 * 未决容量占用情况。
 *
 * 界面据此**提前**禁用提交入口并常驻说明，而不是等用户点下去才拒绝；点击路径里
 * 仍保留 `PendingCapacityError` 兜底（渲染与点击之间容量可能已经被占满）。
 *
 * 计数只看本服务 + 本身份：别的身份占满自己的名额不影响这一页。
 */
export function pendingCapacity(service: string, identity: string): { active: number; limit: number } {
  const active = records.value.filter((item) => isActive(item)
    && item.service === service
    && item.identity === identity).length
  return { active, limit: ACTIVE_LIMIT }
}

/** 失败且结果未知：保持未决，但把已知信息补上。 */
export function leavePendingUnknown(
  token: number | null | undefined,
  patch: { taskId?: string | null; findCode?: ZbaTaskFindCode | null } = {}
): void {
  if (token === null || token === undefined) return
  replace(records.value.map((item) => (item.token === token
    ? {
        ...item,
        phase: 'unresolved',
        taskId: patch.taskId === undefined ? item.taskId : patch.taskId,
        findCode: patch.findCode === undefined ? item.findCode : patch.findCode,
        savedAt: new Date().toISOString()
      }
    : item)))
}

/**
 * 本服务 + 本身份下仍在占用提交入口的关联。
 *
 * 在途（in_flight）与结果未知（unresolved）都算：计划要求"已发送但未确认的操作
 * 转为待核对"，并且不能靠换键来争取成功。返回 savedAt 最新的一条。
 */
export function activePending(service: string, identity: string): PendingIntent | null {
  // records 是"最新在前"：新登记插到最前，回写只改内容不改位置。
  // 所以同一次（savedAt 相同）里第一条就是最新的一条，后续只在严格更新时才替换。
  let found: PendingIntent | null = null
  for (const item of records.value) {
    if (item.service !== service || item.identity !== identity) continue
    if (!isActive(item)) continue
    if (found === null || item.savedAt > found.savedAt) found = item
  }
  return found
}

/** 读回本服务 + 本身份下最近一次未决提交（含已在途的）。 */
export function recallPending(service: string, identity: string): PendingIntent | null {
  return activePending(service, identity)
}

/**
 * 按登记句柄取一条记录。
 *
 * 页面上的一行可能是在**另一个组件实例**里发起的（恢复出来的行、或锁定前那个
 * 实例留下的行），那一次异步流程早就结束了，行里没有可回写的 token。按 key 找到
 * 当前记录，才能让"作为新任务重新提交/收起"这类显式动作真的释放入口。
 */
export function pendingByKey(key: string): PendingIntent | null {
  return records.value.find((item) => item.key === key) ?? null
}

/** 这条关联已经不需要再核对了（找到/明确拒绝/用户显式放弃）。 */
export function forgetPending(key: string): void {
  replace(records.value.filter((item) => item.key !== key))
}

/** 测试与锁定清理用；正常流程不要整体清空。 */
export function clearPending(): void {
  replace([])
}

/**
 * 服务标识：同源 + API 基址。换基址就是换了服务，旧关联一律不可见。
 */
export function serviceLabel(client: { baseUrl: string }): string {
  const origin = (globalThis as { location?: { origin?: string } }).location?.origin ?? 'local'
  return origin + '|' + client.baseUrl
}
