import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TracesPage from '../../src/features/traces/TracesPage.vue'
import { ZbaRequestError, type ZbaClient, type ZbaTracePage, type ZbaTraceSummary } from '../../src/api/zba'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })

function trace(overrides: Partial<ZbaTraceSummary> = {}): ZbaTraceSummary {
  return {
    runId: 't12',
    outcome: 'replied',
    startedAt: '2026-11-03T10:00:00+08:00',
    totalMs: 1234,
    promptTokens: 1200,
    completionTokens: 88,
    fallbackHops: 0,
    cachedPromptTokens: 1024,
    cacheWriteTokens: null,
    cacheUsageUnknown: false,
    nodes: [{ kind: 'Model', status: 'ok', durationMs: 900, toolId: null, reasonCode: null, count: null }],
    ...overrides
  }
}

function page(): ZbaTracePage {
  return {
    items: [
      // 命中 0 与「未知」必须分开渲染：0 是供应商上报的 0 命中。
      trace({ runId: 't12', cachedPromptTokens: 0, cacheWriteTokens: 0 }),
      // null = 供应商没报缓存用量，显示「未知」而不是 0。
      trace({ runId: 't13', cachedPromptTokens: null, cacheWriteTokens: null, cacheUsageUnknown: true })
    ],
    cache: {
      doneTurns: 37,
      activeTurns: 1,
      capacity: 50,
      cachedPromptTokens: 12345,
      cacheWriteTokens: 0,
      cacheUnknownTurns: 4
    }
  }
}

function clientWith(listTraces: () => Promise<ZbaTracePage>): ZbaClient {
  return { baseUrl: '/zba/v1', identityLabel: 'test-identity', listTraces } as unknown as ZbaClient
}

function mountPage(client: ZbaClient) {
  const wrapper = mount(TracesPage, { props: { client }, attachTo: document.body })
  mounted.push(wrapper)
  return wrapper
}

describe('traces page', () => {
  it('把未知轮数与 0 命中分开显示，null 缓存用量显示「未知」', async () => {
    const wrapper = mountPage(clientWith(async () => page()))
    await flushPromises()

    // 汇总：未知轮数是独立一张卡，文案说明「供应商没报缓存用量」。
    const unknownCard = wrapper.get('[data-testid="traces-unknown-turns"]')
    expect(unknownCard.text()).toContain('4')
    expect(unknownCard.text()).toContain('供应商没报缓存用量的轮数')
    // 命中合计 12,345 与写入合计 0 是各自独立的数字。
    expect(wrapper.get('[data-testid="traces-cached-tokens"]').text()).toContain('12,345')
    expect(wrapper.get('[data-testid="traces-cache-write-tokens"]').text()).toContain('0')

    const rows = wrapper.findAll('[data-testid="trace-row"]')
    expect(rows).toHaveLength(2)
    // 供应商明确报了 0 命中：显示 0。
    expect(rows[0]!.get('[data-testid="trace-cache-hit"]').text()).toBe('0')
    expect(rows[0]!.find('[data-testid="trace-cache-unknown"]').exists()).toBe(false)
    // null = 未知，不能折算成 0。
    expect(rows[1]!.get('[data-testid="trace-cache-unknown"]').text()).toBe('未知')
    expect(rows[1]!.find('[data-testid="trace-cache-hit"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="traces-footnote"]').text()).toContain('只读')
  })

  it('没有轨迹时显示空态而不是空表', async () => {
    const wrapper = mountPage(clientWith(async () => ({
      items: [],
      cache: { doneTurns: 0, activeTurns: 0, capacity: 50, cachedPromptTokens: 0, cacheWriteTokens: 0, cacheUnknownTurns: 0 }
    })))
    await flushPromises()

    expect(wrapper.get('[data-testid="traces-empty"]').text()).toContain('还没有轨迹记录')
    expect(wrapper.findAll('[data-testid="trace-row"]')).toHaveLength(0)
    // 空明细仍然显示汇总（0 不是未知）。
    expect(wrapper.get('[data-testid="traces-unknown-turns"]').text()).toContain('0')
  })

  it('读取失败时显示错误文案，且不渲染半成品明细', async () => {
    const wrapper = mountPage(clientWith(async () => {
      throw new ZbaRequestError(500, 'internal_error', '轨迹读取失败（合成）')
    }))
    await flushPromises()

    const notice = wrapper.get('[data-testid="error-notice"]')
    expect(notice.text()).toContain('轨迹读取失败')
    expect(notice.text()).toContain('ZBA 服务内部错误。')
    expect(notice.text()).toContain('internal_error')
    expect(wrapper.find('[data-testid="trace-row"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="traces-empty"]').exists()).toBe(false)
  })
})
