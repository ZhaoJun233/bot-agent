import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import CapabilitiesPage from '../../src/features/capabilities/CapabilitiesPage.vue'
import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaManagedDescriptor,
  type ZbaManagedPage
} from '../../src/api/zba'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
})

const descriptor = (overrides: Partial<ZbaManagedDescriptor> = {}): ZbaManagedDescriptor => ({
  key: { kind: 'plugin', key: 'preset.feature.music' },
  displayName: 'plugin',
  desiredEnabled: true,
  runtimeState: 4,
  actions: [
    { action: 3, supported: false, reasonCode: 'configuration_required' },
    { action: 4, supported: true, reasonCode: '' }
  ],
  credentialConfigured: true,
  descriptorRevision: 'rev-1',
  ...overrides
})

const readResult = {
  code: 0,
  version: '1',
  generation: 'e6fe12b715524927a91bb01af7857bdb',
  contractRevision: 'cfg-0.3.3',
  serverUnixSeconds: 1760000000,
  secrets: [],
  fields: [],
  latestApplyState: 1
}

function page(overrides: Partial<ZbaManagedPage> = {}): ZbaManagedPage {
  return { code: 0, items: [descriptor()], nextCursor: null, descriptorRevision: 'rev-1', ...overrides }
}

function clientWith(overrides: Partial<Record<string, unknown>> = {}): ZbaClient {
  return {
    readConfiguration: async () => readResult,
    listCapabilities: async () => page(),
    inspectCapability: async () => ({ code: 0, descriptor: descriptor() }),
    executeCapability: async () => ({
      code: 0,
      replayed: false,
      receipt: {
        key: 'mg1:e6fe12b715524927a91bb01af7857bdb:1760000000:0123456789abcdef0123456789abcdef',
        target: { kind: 'plugin', key: 'preset.feature.music' },
        action: 4,
        descriptorRevision: 'rev-1',
        state: 2
      }
    }),
    queryCapability: async () => ({ code: 0, replayed: true }),
    cancelCapability: async () => ({ code: 0, state: 4 }),
    ...overrides
  } as unknown as ZbaClient
}

function mountPage(client = clientWith(), mode: 'read' | 'admin' = 'admin') {
  const wrapper = mount(CapabilitiesPage, {
    props: { client, mode, writesEnabled: true },
    attachTo: document.body
  })
  mounted.push(wrapper)
  return wrapper
}

describe('CapabilitiesPage', () => {
  it('渲染能力卡片、运行时状态与支持矩阵', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('[data-testid="capabilities"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-mode-badge"]').text()).toBe('管理员模式')
    expect(wrapper.get('[data-testid="cap-count"]').text()).toContain('1')
    expect(wrapper.get('[data-testid="cap-item-plugin:preset.feature.music"]').text()).toContain('运行中')
    expect(wrapper.get('[data-testid="cap-item-plugin:preset.feature.music"]').text()).toContain('已配置')
    // 支持矩阵：停止可用，启动带原因码
    expect(wrapper.find('[data-testid="cap-action-plugin:preset.feature.music-4"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-reason-plugin:preset.feature.music-3"]').text()).toContain('configuration_required')
  })

  it('标准模式可浏览但不能执行动作', async () => {
    const wrapper = mountPage(clientWith(), 'read')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-readonly"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-mode-badge"]').text()).toBe('标准模式')
    expect(wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').attributes('disabled')).toBeDefined()
  })

  it('危险动作先二次确认，确认后才提交并展示回执', async () => {
    const calls: unknown[] = []
    const client = clientWith({
      executeCapability: async (request: unknown) => {
        calls.push(request)
        return {
          code: 0,
          replayed: false,
          receipt: {
            key: (request as { key: string }).key,
            target: { kind: 'plugin', key: 'preset.feature.music' },
            action: 4,
            descriptorRevision: 'rev-1',
            state: 2
          }
        }
      }
    })
    const wrapper = mountPage(client)
    await flushPromises()

    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    expect(calls).toHaveLength(0)
    const dialog = document.body.querySelector<HTMLElement>('[data-testid="cap-confirm"]')
    expect(dialog?.textContent).toContain('危险动作')

    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    await flushPromises()

    expect(calls).toHaveLength(1)
    const request = calls[0] as { contractRevision: string; command: unknown; key: string }
    expect(request.contractRevision).toBe('cfg-0.3.3')
    expect(request.key.startsWith('mg1:')).toBe(true)
    expect(request.command).toMatchObject({ kind: 'plugin', key: 'preset.feature.music', action: 1 })
    expect(wrapper.get('[data-testid="cap-result-code"]').text()).toBe('0')
    expect(wrapper.get('[data-testid="cap-result-state"]').text()).toContain('已完成')
  })

  it('取消确认不会提交动作', async () => {
    const calls: unknown[] = []
    const wrapper = mountPage(clientWith({ executeCapability: async (r: unknown) => { calls.push(r); return { code: 0, replayed: false } } }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-cancel"]')?.click()
    await flushPromises()
    expect(calls).toHaveLength(0)
    expect(document.body.querySelector('[data-testid="cap-confirm"]')).toBeNull()
  })

  it('动作结果未知时保留操作键并提示核对，不写"已完成"', async () => {
    const wrapper = mountPage(clientWith({
      executeCapability: async () => ({ code: 14, replayed: false })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()

    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cap-result-state"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="cap-result-query"]').exists()).toBe(true)
  })

  it('列表读取失败显示安全错误且不渲染卡片', async () => {
    const wrapper = mountPage(clientWith({
      listCapabilities: async () => { throw new ZbaRequestError(403, 'forbidden', '需要管理员。') }
    }))
    await flushPromises()
    expect(wrapper.text()).toContain('读取能力失败')
    expect(wrapper.text()).toContain('当前令牌没有该操作权限。')
    expect(wrapper.find('[data-testid="cap-item-plugin:preset.feature.music"]').exists()).toBe(false)
  })

  it('翻页失败保留已加载列表并显示游标错误', async () => {
    let calls = 0
    const wrapper = mountPage(clientWith({
      listCapabilities: async () => {
        calls += 1
        if (calls === 1) return page({ nextCursor: 'cursor-1' })
        throw new ZbaRequestError(400, 'invalid_query', '请求无效。')
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-load-more"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="cap-page-error"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-page-error"]').text()).toContain('下一页没有加载')
    // 已加载的列表不能丢
    expect(wrapper.get('[data-testid="cap-count"]').text()).toContain('1')
  })

  it('配置读取失败时禁止提交动作', async () => {
    const wrapper = mountPage(clientWith({
      readConfiguration: async () => { throw new ZbaRequestError(503, 'storage_unavailable', '暂不可用。') }
    }))
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-context-error"]').exists()).toBe(true)
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    expect(document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.disabled).toBe(true)
  })


  /**
   * S4-R1：在途动作不能把新确认意图的退出路径一起锁死。
   *
   * 组件级只验不变量本身：框里有可用按钮、取消不需要等请求、取消不发写请求。
   * 浏览器里的焦点与 Escape 由 tests/e2e/capabilities.spec.ts 覆盖。
   */
  it('上一个动作在途时，新确认框的取消仍然可用且不发写请求', async () => {
    const writes: unknown[] = []
    let release!: () => void
    const held = new Promise<void>((resolve) => { release = resolve })
    let entered!: () => void
    const seen = new Promise<void>((resolve) => { entered = resolve })
    const second = descriptor({ key: { kind: 'plugin', key: 'preset.feature.stickers' } })
    const wrapper = mountPage(clientWith({
      listCapabilities: async () => ({ code: 0, items: [descriptor(), second], nextCursor: null, descriptorRevision: 'rev-1' }),
      inspectCapability: async () => {
        entered()
        await held
        throw new ZbaRequestError(503, 'storage_unavailable', '暂不可用。')
      },
      executeCapability: async (request: unknown) => { writes.push(request); return { code: 0, replayed: false } }
    }))
    await flushPromises()

    // 第一个动作：确认后卡在预检上。
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await seen
    await flushPromises()
    expect(document.body.querySelector('[data-testid="cap-confirm"]')).toBeNull()

    // 第二个动作：在途窗口里打开。
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.stickers-4"]').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('[data-testid="cap-confirm"]')
    expect(dialog).not.toBeNull()
    const usable = Array.from(dialog!.querySelectorAll('button')).filter((button) => !button.disabled)
    expect(usable.length, '在途时新框至少要有一个可用按钮').toBeGreaterThan(0)
    expect(document.body.querySelector('[data-testid="cap-confirm-busy"]')).not.toBeNull()

    // 取消必须立刻生效，不需要先释放上一个请求。
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-cancel"]')?.click()
    await flushPromises()
    expect(document.body.querySelector('[data-testid="cap-confirm"]')).toBeNull()
    expect(writes, '取消新意图不得发出写请求').toHaveLength(0)

    release()
    await flushPromises()
    await flushPromises()
  })

  it('401 上报会话失效，不在本页吞掉', async () => {
    const wrapper = mountPage(clientWith({
      listCapabilities: async () => { throw new ZbaRequestError(401, 'unauthorized', 'expired') }
    }))
    await flushPromises()
    const emitted = wrapper.emitted('auth-expired')
    expect(emitted).toHaveLength(1)
    expect((emitted?.[0]?.[0] as { code: string }).code).toBe('unauthorized')
  })
// ── S23-R2 恢复路径 ────────────────────────────────────────────────────────

  it('丢响应后仍能看到核对入口，重试沿用同一个操作键', async () => {
    const keys: string[] = []
    const wrapper = mountPage(clientWith({
      executeCapability: async (request: unknown) => {
        keys.push((request as { key: string }).key)
        throw new ZbaNetworkError('timeout')
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()

    // 确认框不能继续挡着：它退场后核对入口才点得到。
    expect(document.body.querySelector('[data-testid="cap-confirm"]')).toBeNull()
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cap-result-query"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(keys[0])

    await wrapper.get('[data-testid="cap-confirm-ok"]').trigger('click')
    await flushPromises()
    expect(keys).toHaveLength(2)
    expect(keys[1]).toBe(keys[0])
  })

  it('未知结果不会因为打开另一个动作而丢掉操作键', async () => {
    const queries: string[] = []
    const wrapper = mountPage(clientWith({
      executeCapability: async () => ({ code: 14, replayed: false }),
      queryCapability: async (key: string) => {
        queries.push(key)
        return { code: 0, replayed: false }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    const firstKey = wrapper.get('[data-testid="cap-op-key"]').text()

    // 打开另一个动作的确认框再取消：旧键必须还在。
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    // 确认框 Teleport 到 body，复用提示要在 document 上找。
    expect(document.body.querySelector('[data-testid="cap-confirm-reuse"]')).not.toBeNull()
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-cancel"]')?.click()
    await flushPromises()

    // 旧操作仍是当前回执：键没变，也没有被新动作顶掉。
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(firstKey)
    await wrapper.get('[data-testid="cap-result-query"]').trigger('click')
    await flushPromises()
    expect(queries).toEqual([firstKey])
  })

  it('迟到的一次核对不会覆盖另一个动作的回执', async () => {
    let release!: () => void
    const wrapper = mountPage(clientWith({
      executeCapability: async () => ({ code: 14, replayed: false }),
      queryCapability: async () => {
        await new Promise<void>((resolve) => { release = resolve })
        return {
          code: 0,
          replayed: false,
          receipt: {
            key: 'other',
            target: { kind: 'plugin', key: 'preset.feature.music' },
            action: 4,
            descriptorRevision: 'rev-1',
            state: 2
          }
        }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    const firstKey = wrapper.get('[data-testid="cap-op-key"]').text()

    await wrapper.get('[data-testid="cap-result-query"]').trigger('click')
    await flushPromises()
    release()
    await flushPromises()
    // 回执写回的是发起查询的那条记录，当前展示的操作键没被换掉。
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(firstKey)
    expect(wrapper.get('[data-testid="cap-result-state"]').text()).toContain('已完成')
  })

  it('卸载后迟到的 inspect 不得再发出执行请求', async () => {
    let release!: () => void
    const executes: unknown[] = []
    const wrapper = mountPage(clientWith({
      inspectCapability: async () => {
        await new Promise<void>((resolve) => { release = resolve })
        return { code: 0, descriptor: descriptor() }
      },
      executeCapability: async (request: unknown) => {
        executes.push(request)
        return { code: 0, replayed: false }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    wrapper.unmount()
    release()
    await flushPromises()
    expect(executes).toHaveLength(0)
  })

  it('降级为标准模式后迟到的 inspect 不得再发出执行请求', async () => {
    let release!: () => void
    const executes: unknown[] = []
    const wrapper = mountPage(clientWith({
      inspectCapability: async () => {
        await new Promise<void>((resolve) => { release = resolve })
        return { code: 0, descriptor: descriptor() }
      },
      executeCapability: async (request: unknown) => {
        executes.push(request)
        return { code: 0, replayed: false }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    await wrapper.setProps({ mode: 'read' })
    release()
    await flushPromises()
    expect(executes).toHaveLength(0)
  })

  it('加载中禁用筛选，避免按钮与列表不一致', async () => {
    let release!: () => void
    const wrapper = mountPage(clientWith({
      listCapabilities: async () => {
        await new Promise<void>((resolve) => { release = resolve })
        return page()
      }
    }))
    await flushPromises()
    expect(wrapper.get('[data-testid="cap-filter-1"]').attributes('disabled')).toBeDefined()
    release()
    await flushPromises()
    expect(wrapper.get('[data-testid="cap-filter-1"]').attributes('disabled')).toBeUndefined()
  })

  it('同名插件与供应商是两个不同身份，不会互相覆盖', async () => {
    const plugin = descriptor()
    const provider = descriptor({
      key: { kind: 'provider', key: 'preset.feature.music' },
      displayName: 'provider'
    })
    const wrapper = mountPage(clientWith({
      listCapabilities: async () => page({ items: [plugin, provider] }),
      inspectCapability: async () => ({ code: 0, descriptor: { ...provider, displayName: 'provider-refreshed' } })
    }))
    await flushPromises()
    expect(wrapper.findAll('.cap-card')).toHaveLength(2)
    expect(wrapper.find('[data-testid="cap-inspect-provider:preset.feature.music"]').exists()).toBe(true)
    await wrapper.get('[data-testid="cap-inspect-provider:preset.feature.music"]').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.cap-card')).toHaveLength(2)
    expect(wrapper.find('[data-testid="cap-item-plugin:preset.feature.music"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-item-provider:preset.feature.music"]').text()).toContain('provider-refreshed')
  })
// ── S23-R2-F1 / S23-OWN-F1 / 无回执查询错误 ─────────────────────────────────

  it('未知结果的卡片重开：预检失败不否定原提交，键与面板都保留', async () => {
    const keys: string[] = []
    let inspects = 0
    const wrapper = mountPage(clientWith({
      inspectCapability: async () => {
        inspects += 1
        if (inspects > 1) throw new ZbaNetworkError('timeout')
        return { code: 0, descriptor: descriptor() }
      },
      executeCapability: async (request: unknown) => {
        keys.push((request as { key: string }).key)
        throw new ZbaNetworkError('timeout')
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    const firstKey = wrapper.get('[data-testid="cap-op-key"]').text()
    expect(keys).toEqual([firstKey])

    // 同一张动作卡片重开再确认：这次是提交前核对失败。
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    expect(document.body.querySelector('[data-testid="cap-confirm-reuse"]')).not.toBeNull()
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()

    // 不能因此变成「已拒绝」：上一次提交仍然未知，键还是那个键，也没有第二次提交。
    expect(keys).toHaveLength(1)
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cap-result-preparing"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(firstKey)

    // 关闭：未终态记录既不能删也不能收起。
    await wrapper.get('[data-testid="cap-result-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(firstKey)
    expect(wrapper.find('[data-testid="cap-result-pinned"]').exists()).toBe(true)
  })

  it('未知结果重发沿用第一次提交的命令与描述符版本', async () => {
    const requests: Array<{ key: string; command: unknown; expectedDescriptorRevision: string }> = []
    let inspects = 0
    const wrapper = mountPage(clientWith({
      // 第一次提交时是 rev-1；之后描述符已经变成 rev-9，重发不能用它去拼新内容。
      inspectCapability: async () => {
        inspects += 1
        return { code: 0, descriptor: descriptor({ descriptorRevision: inspects === 1 ? 'rev-1' : 'rev-9' }) }
      },
      executeCapability: async (request: unknown) => {
        const body = request as { key: string; command: unknown; expectedDescriptorRevision: string }
        requests.push({
          key: body.key,
          command: body.command,
          expectedDescriptorRevision: body.expectedDescriptorRevision
        })
        if (requests.length === 1) throw new ZbaNetworkError('timeout')
        return {
          code: 0,
          replayed: true,
          receipt: {
            key: body.key,
            target: { kind: 'plugin', key: 'preset.feature.music' },
            action: 4,
            descriptorRevision: 'rev-1',
            state: 2
          }
        }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    expect(requests).toHaveLength(1)

    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()

    expect(requests).toHaveLength(2)
    expect(requests[1]?.key).toBe(requests[0]?.key)
    expect(requests[1]?.command).toEqual(requests[0]?.command)
    // 冻结值 rev-1，而不是重新核对拿到的 rev-9。
    expect(requests[1]?.expectedDescriptorRevision).toBe('rev-1')
  })

  it('切走再切回来，迟到的 inspect 不得再发出执行请求', async () => {
    let release!: () => void
    const executes: unknown[] = []
    const wrapper = mountPage(clientWith({
      inspectCapability: async () => {
        await new Promise<void>((resolve) => { release = resolve })
        return { code: 0, descriptor: descriptor() }
      },
      executeCapability: async (request: unknown) => {
        executes.push(request)
        return { code: 0, replayed: false }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()

    // 离开视图再回来：active 又变回 true，旧确认必须已经永久失效。
    await wrapper.setProps({ active: false })
    await wrapper.setProps({ active: true })
    release()
    await flushPromises()
    expect(executes).toHaveLength(0)
  })

  it('无回执的核对错误不会覆盖运行中的回执', async () => {
    const wrapper = mountPage(clientWith({
      executeCapability: async () => ({
        code: 0,
        replayed: false,
        receipt: {
          key: 'mg1:e6fe12b715524927a91bb01af7857bdb:1760000000:0123456789abcdef0123456789abcdef',
          target: { kind: 'plugin', key: 'preset.feature.music' },
          action: 4,
          descriptorRevision: 'rev-1',
          state: 1
        }
      }),
      queryCapability: async () => ({ code: 13, replayed: false })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    expect(wrapper.get('[data-testid="cap-result-state"]').text()).toContain('执行中')

    await wrapper.get('[data-testid="cap-result-query"]').trigger('click')
    await flushPromises()

    // 存储暂不可用只是「这次没核对到」，不是「那个操作结束了」。
    expect(wrapper.get('[data-testid="cap-result-state"]').text()).toContain('执行中')
    await wrapper.get('[data-testid="cap-result-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cap-result-pinned"]').exists()).toBe(true)
  })

  it('核对网络失败时未知记录仍是未知，不会被判成已结束', async () => {
    const wrapper = mountPage(clientWith({
      executeCapability: async () => { throw new ZbaNetworkError('timeout') },
      queryCapability: async () => { throw new ZbaNetworkError('timeout') }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    await wrapper.get('[data-testid="cap-result-query"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="cap-result-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)
  })

// ── S23-R2-F2：未知历史不被「无回执拒绝」抹掉 ───────────────────────────────

  it('未知操作同键重发收到无回执拒绝后，仍保留旧键与核对入口', async () => {
    const keys: string[] = []
    let call = 0
    const wrapper = mountPage(clientWith({
      executeCapability: async (request: unknown) => {
        keys.push((request as { key: string }).key)
        call += 1
        // 第一次：响应丢失，结果未知（服务端可能已经受理）。
        if (call === 1) throw new ZbaNetworkError('timeout')
        // 之后：同键重发被服务端重新授权后拒绝，且没有回执。
        return { code: 1, replayed: false }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    const key = wrapper.get('[data-testid="cap-op-key"]').text()
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)

    // 专用重试入口：同键重发被拒。
    await wrapper.get('[data-testid="cap-confirm-ok"]').trigger('click')
    await flushPromises()

    // 拒绝不能把这次操作降级成「确认没执行」。
    expect(keys).toHaveLength(2)
    expect(keys[1]).toBe(key)
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)
    await wrapper.get('[data-testid="cap-result-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(key)
    expect(wrapper.find('[data-testid="cap-result-query"]').exists()).toBe(true)
  })

  it('全新操作被明确拒绝时仍然可以关闭（不误判成未知）', async () => {
    const wrapper = mountPage(clientWith({
      executeCapability: async () => ({ code: 1, replayed: false })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(false)
    await wrapper.get('[data-testid="cap-result-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result"]').exists()).toBe(false)
  })

  it('未知操作卡片重开后被拒绝，同样保留旧键', async () => {
    const keys: string[] = []
    let call = 0
    const wrapper = mountPage(clientWith({
      executeCapability: async (request: unknown) => {
        keys.push((request as { key: string }).key)
        call += 1
        if (call === 1) throw new ZbaNetworkError('timeout')
        return { code: 1, replayed: false }
      }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    const key = wrapper.get('[data-testid="cap-op-key"]').text()

    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()

    expect(keys).toHaveLength(2)
    expect(keys[1]).toBe(key)
    expect(wrapper.find('[data-testid="cap-result-unknown"]').exists()).toBe(true)
    await wrapper.get('[data-testid="cap-result-close"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="cap-result"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="cap-op-key"]').text()).toBe(key)
  })

  it('核对返回 code0 但无回执时不覆盖已知的运行中回执', async () => {
    const wrapper = mountPage(clientWith({
      executeCapability: async () => ({
        code: 0,
        replayed: false,
        receipt: {
          key: 'mg1:e6fe12b715524927a91bb01af7857bdb:1760000000:0123456789abcdef0123456789abcdef',
          target: { kind: 'plugin', key: 'preset.feature.music' },
          action: 4,
          descriptorRevision: 'rev-1',
          state: 1
        }
      }),
      queryCapability: async () => ({ code: 0, replayed: false })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="cap-action-plugin:preset.feature.music-4"]').trigger('click')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cap-confirm-ok"]')?.click()
    await flushPromises()
    expect(wrapper.get('[data-testid="cap-result-state"]').text()).toContain('执行中')

    await wrapper.get('[data-testid="cap-result-query"]').trigger('click')
    await flushPromises()
    // 没有回执的核对结果不能把「执行中」抹掉。
    expect(wrapper.get('[data-testid="cap-result-state"]').text()).toContain('执行中')
  })
})