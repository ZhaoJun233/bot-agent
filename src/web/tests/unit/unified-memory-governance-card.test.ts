import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient, ZbaRequestError } from '../../src/api/zba'
import UnifiedMemoryGovernanceCard from '../../src/features/configuration/UnifiedMemoryGovernanceCard.vue'
import { stateName } from '../../src/shared/friendly'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
})

describe('UnifiedMemoryGovernanceCard', () => {
  it('renders aggregate unified-memory governance counters without any body text', async () => {
    const client = new ZbaClient({ baseUrl: '/synthetic/host', token: 'synthetic-token' })
    vi.spyOn(client, 'status').mockResolvedValue({
      components: [
        {
          id: 'unified-memory',
          kind: 'memory',
          state: 'enabled',
          available: true,
          reasonCode: 'rev=7;confirmed=3;pending=1;suppressed=2;admin=1'
        }
      ],
      overallHealth: 'ok',
      version: '1.0.0',
      startedAt: '2026-11-22T00:00:00Z',
      runtimeMode: 'headless',
      configurationVersion: '1',
      descriptorRevision: 'rev',
      restartRequired: false,
      writesEnabled: true
    } as never)

    const wrapper = mount(UnifiedMemoryGovernanceCard, { props: { client, active: true } })
    mounted.push(wrapper)
    await flushPromises()

    expect(wrapper.get('[data-testid="unified-memory-governance-card"]').text()).toContain('已启用')
    expect(wrapper.get('[data-testid="umem-confirmed-count"]').text()).toBe('3')
    expect(wrapper.get('[data-testid="umem-pending-count"]').text()).toBe('1')
    expect(wrapper.get('[data-testid="umem-suppressed-count"]').text()).toBe('2')
    expect(wrapper.get('[data-testid="umem-admin-status"]').text()).toBe('已配置')
    expect(wrapper.text()).toContain('管理员控制台绝不提供他人候选或事实正文列表')
  })

  it('emits auth-expired cleanly on 401 status errors', async () => {
    const client = new ZbaClient({ baseUrl: '/synthetic/host', token: 'synthetic-token' })
    vi.spyOn(client, 'status').mockRejectedValue(new ZbaRequestError(401, 'unauthorized', 'auth expired'))
    const wrapper = mount(UnifiedMemoryGovernanceCard, { props: { client, active: true } })
    mounted.push(wrapper)
    await flushPromises()

    expect(wrapper.emitted('auth-expired')).toHaveLength(1)
  })

  it('renders fail-closed fault banner when governance_query_failed and maps enabled state label', async () => {
    expect(stateName('enabled')).toBe('已启用')

    const client = new ZbaClient({ baseUrl: '/synthetic/host', token: 'synthetic-token' })
    vi.spyOn(client, 'status').mockResolvedValue({
      components: [
        {
          id: 'unified-memory',
          kind: 'governance',
          displayName: '统一记忆治理',
          state: 'faulted',
          available: false,
          reasonCode: 'governance_query_failed',
          updatedAt: '2026-11-22T00:00:00Z'
        }
      ],
      ready: false,
      overallHealth: 'degraded',
      version: '1.0.0',
      startedAt: '2026-11-22T00:00:00Z',
      runtimeMode: 'headless',
      configurationVersion: '1',
      descriptorRevision: 'rev',
      restartRequired: false,
      writesEnabled: true
    } as never)

    const wrapper = mount(UnifiedMemoryGovernanceCard, { props: { client, active: true } })
    mounted.push(wrapper)
    await flushPromises()

    expect(wrapper.get('[data-testid="unified-memory-governance-card"]').text()).toContain('状态读取失败（Fail-Closed）')
    expect(wrapper.get('[data-testid="umem-gov-fault"]').text()).toContain('Fail-Closed')
  })
})
