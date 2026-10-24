import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

/**
 * App 的会话归属（LIFE-04）。
 *
 * 手动 load() 走的是「health → meta → status」三段读取；验收方证明它在 meta 与
 * status 之间缺少归属复核：A 的 meta 还挂着时切到 B，A 的 meta 一落地就仍然向旧
 * client 发一次 status 只读。reloadStatus 那条后台链路早就补上了这道闸，手动这条
 * 不能只靠最后的成功写回 guard —— 那次多余的旧身份请求本身就该在发出前被拦掉。
 *
 * 这里只替换 ZbaClient 的构造，其余契约类型/错误类型都走真实实现。
 */

const clients = new Map<string, Record<string, unknown>>()
let constructed: string[] = []

vi.mock('../../src/api/zba', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../src/api/zba')>()
  class FakeZbaClient {
    readonly baseUrl = '/zba/v1'
    readonly identityLabel: string = 'synthetic'
    constructor(options: { token?: string } = {}) {
      const token = options.token ?? ''
      constructed.push(token)
      const impl = clients.get(token)
      // 未登记令牌（空令牌）也要有个最小可用实现。
      return { baseUrl: '/zba/v1', identityLabel: 'id-' + token, ...(impl ?? blank()) } as never
    }
  }
  return { ...actual, ZbaClient: FakeZbaClient }
})

/** 完整形状的 wire 对象：缺字段会触发真实的 Vue prop 警告，掩盖真正的失败。 */
function statusOf(state: string) {
  return {
    state,
    ready: true,
    version: '1.0.0',
    startedAt: '2026-10-24T00:00:00Z',
    updatedAt: '2026-10-24T00:00:00Z',
    configurationVersion: 'synthetic-v1',
    configurationApplyState: 'applied',
    components: [],
    recentErrors: []
  }
}

function metaOf(instanceId: string) {
  return {
    contractVersion: 'zba-1.0',
    serverVersion: '1.0.0',
    authentication: 'token',
    scopes: ['read', 'admin'],
    sse: false,
    mcpManagement: false,
    dockerManagement: false,
    maxBodyBytes: 1048576,
    instanceId,
    features: []
  }
}

function blank(): Record<string, unknown> {
  return {
    health: async () => ({ status: 'ok', configured: true, ready: true, contractVersion: 'zba-1.0' }),
    meta: async () => metaOf('blank'),
    status: async () => statusOf('blank'),
    listEvents: async (after: number) => ({
      items: [], nextSequence: after, resyncRequired: false, generatedAt: '2026-10-24T00:00:00Z'
    })
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function sessionClient(id: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const counts = { status: 0 }
  const client = {
    ...blank(),
    identityLabel: id,
    counts,
    meta: async () => metaOf(id),
    status: async () => { counts.status += 1; return statusOf(id) },
    ...overrides
  }
  return client
}

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
  clients.clear()
  constructed = []
})

async function mountApp() {
  const App = (await import('../../src/App.vue')).default
  const wrapper = mount(App, { attachTo: document.body })
  mounted.push(wrapper)
  await flushPromises()
  return wrapper
}

describe('App 会话归属（LIFE-04）', () => {
  it('手动 load 的 meta 与 status 之间复核归属：旧身份不再发第二次读取', async () => {
    const a = sessionClient('A')
    const b = sessionClient('B')
    const metaResponse = deferred<unknown>()
    a.meta = () => metaResponse.promise
    clients.set('A', a); clients.set('B', b)

    const wrapper = await mountApp()
    // script setup 的绑定在 vm 上是自动解包的（ref 已经展开成值）。
    const vm = wrapper.vm as unknown as {
      beginSession: (value: string) => number
      clearSession: () => void
      load: () => Promise<void>
      token: string
    }

    vm.beginSession('A')
    await flushPromises()
    const reading = vm.load()
    await flushPromises()

    // meta 还挂着的时候切身份：会话代数推进，A 的这次 load 已经不再拥有会话。
    vm.clearSession()
    vm.beginSession('B')
    await flushPromises()

    metaResponse.resolve(metaOf('A'))
    await reading
    await flushPromises()

    // 关键断言：A 的 status 一次都没发出去（不是"发了但没写回"）。
    expect((a.counts as { status: number }).status).toBe(0)
    expect(vm.token).toBe('B')
  })

  it('同一会话内 meta 与 status 都正常落地', async () => {
    const a = sessionClient('A')
    clients.set('A', a)
    const wrapper = await mountApp()
    const vm = wrapper.vm as unknown as {
      beginSession: (value: string) => number
      load: () => Promise<void>
      token: string
    }
    vm.beginSession('A')
    await flushPromises()
    await vm.load()
    await flushPromises()
    // 同一会话内三段读取都发出去了：meta 与 status 各一次。
    expect((a.counts as { status: number }).status).toBe(1)
    expect(vm.token).toBe('A')
  })
})
