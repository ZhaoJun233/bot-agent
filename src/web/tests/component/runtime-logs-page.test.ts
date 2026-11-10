import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import RuntimeLogsPage from '../../src/features/logs/RuntimeLogsPage.vue'
import { ZbaNetworkError, ZbaRequestError, type ZbaClient, type ZbaRuntimeLogFeed } from '../../src/api/zba'

const mounted: Array<{ unmount(): void }> = []
afterEach(() => { mounted.splice(0).forEach(w => w.unmount()); vi.useRealTimers(); vi.restoreAllMocks() })
const epoch = 'a'.repeat(32)
type FeedReader = (cursor?: string, signal?: AbortSignal) => Promise<ZbaRuntimeLogFeed>
function page(start = 1, count = 3, hasMore = false): ZbaRuntimeLogFeed {
  return { capacity: 600, cursor: 'cursor-' + (start + count - 1), hasMore, items: Array.from({ length: count }, (_, index) => {
    const number = start + index
    return { id: epoch + ':' + number, sequence: String(number), time: 1793773800000 + number,
      level: number % 2 === 0 ? 'warn' : 'info', source: 'Agent', message: '安全摘要 ' + number, redacted: true }
  }) }
}
function mountPage(read = vi.fn<FeedReader>(async () => page())) {
  const client = { listRuntimeLogFeed: read } as unknown as ZbaClient
  const wrapper = mount(RuntimeLogsPage, { props: { client, active: true }, attachTo: document.body })
  mounted.push(wrapper)
  return { wrapper, read, client }
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

describe('运行日志实时安全终端', () => {
  it('选择文字暂停跟随但持续接收，并明确披露重置缺口', async () => {
    vi.useFakeTimers()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockResolvedValueOnce({ ...page(10, 1), resetReason: 'expired', omittedCount: 6 })
    const { wrapper } = mountPage(read)
    await flushPromises()
    const range = document.createRange()
    range.selectNodeContents(wrapper.get('.log-message').element)
    const selection = document.getSelection()!
    selection.removeAllRanges(); selection.addRange(range)
    document.dispatchEvent(new Event('selectionchange'))
    await flushPromises()
    expect(wrapper.text()).toContain('阅读中 · 接收不受影响')
    expect((wrapper.find('input[type=checkbox]').element as HTMLInputElement).checked).toBe(true)
    await vi.advanceTimersByTimeAsync(1000)
    expect(read).toHaveBeenCalledTimes(2)
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(4)
    expect(wrapper.text()).toContain('遗漏 6 条')
    selection.removeAllRanges()
  })
  it('从旧到新显示，纯文本渲染，筛选不请求后端', async () => {
    const data = page()
    data.items[0]!.message = '<img src=x onerror=alert(1)>'
    const { wrapper, read } = mountPage(vi.fn<FeedReader>(async () => data))
    await flushPromises()
    expect(wrapper.findAll('[data-log-id]').map(row => row.attributes('data-log-id'))).toEqual([epoch + ':1', epoch + ':2', epoch + ':3'])
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    await wrapper.get('[aria-label="日志级别"]').setValue('warn')
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(1)
    expect(read).toHaveBeenCalledTimes(1)
  })
  it('每秒串行增量，慢请求不重叠，hasMore 立即排空', async () => {
    vi.useFakeTimers()
    const pending = deferred<ZbaRuntimeLogFeed>()
    const read = vi.fn<FeedReader>().mockReturnValueOnce(pending.promise).mockResolvedValueOnce(page(4, 2, true)).mockResolvedValue(page(6, 1))
    const { wrapper } = mountPage(read)
    await vi.advanceTimersByTimeAsync(5000)
    expect(read).toHaveBeenCalledTimes(1)
    pending.resolve(page())
    await flushPromises()
    await vi.advanceTimersByTimeAsync(999)
    expect(read).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(read).toHaveBeenCalledTimes(3)
    expect(read.mock.calls[1]?.[0]).toBe('cursor-3')
    expect(read.mock.calls[2]?.[0]).toBe('cursor-5')
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(6)
  })
  it('后台和失活取消请求，迟到响应不写回，返回继续游标', async () => {
    vi.useFakeTimers()
    const pending = deferred<ZbaRuntimeLogFeed>()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockReturnValueOnce(pending.promise).mockResolvedValue(page(4, 1))
    const { wrapper } = mountPage(read)
    await flushPromises()
    await vi.advanceTimersByTimeAsync(1000)
    const signal = read.mock.calls[1]?.[1] as AbortSignal
    await wrapper.setProps({ active: false })
    expect(signal.aborted).toBe(true)
    pending.resolve(page(20, 1))
    await flushPromises()
    expect(wrapper.text()).not.toContain('安全摘要 20')
    await vi.advanceTimersByTimeAsync(10000)
    expect(read).toHaveBeenCalledTimes(2)
    await wrapper.setProps({ active: true })
    await flushPromises()
    expect(read.mock.calls[2]?.[0]).toBe('cursor-3')
  })
  it('隐藏和卸载都取消请求', async () => {
    const read = vi.fn<FeedReader>(() => new Promise<ZbaRuntimeLogFeed>(() => {}))
    const { wrapper } = mountPage(read)
    const firstSignal = read.mock.calls[0]?.[1] as AbortSignal
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(firstSignal.aborted).toBe(true)
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible')
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    const lastSignal = read.mock.calls.at(-1)?.[1] as AbortSignal
    wrapper.unmount()
    expect(lastSignal.aborted).toBe(true)
  })
  it('客户端切换和锁定清除安全缓存，不接纳旧端点迟到结果', async () => {
    const late = deferred<ZbaRuntimeLogFeed>()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockReturnValue(late.promise)
    const { wrapper } = mountPage(read)
    await flushPromises()
    const reload = (wrapper.vm as unknown as { reload(): Promise<void> }).reload()
    const replacement = vi.fn<FeedReader>(async () => page(100, 1))
    await wrapper.setProps({ client: { listRuntimeLogFeed: replacement } as unknown as ZbaClient })
    late.resolve(page(20, 1)); await reload; await flushPromises()
    expect(wrapper.text()).not.toContain('安全摘要 20')
    expect(wrapper.text()).toContain('安全摘要 100')
    await wrapper.setProps({ locked: true })
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(0)
  })
  it.each([401, 403])('HTTP %i 清空缓存并停止，不接受刷新复活', async status => {
    vi.useFakeTimers()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockRejectedValue(new ZbaRequestError(status, 'unauthorized', '无权限'))
    const { wrapper } = mountPage(read)
    await flushPromises(); await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(0)
    expect(wrapper.emitted('auth-expired')).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(60000)
    expect(read).toHaveBeenCalledTimes(2)
  })
  it('网络失败保留安全行，按 1/2/4/8/15 秒重试', async () => {
    vi.useFakeTimers()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockRejectedValue(new ZbaNetworkError('offline'))
    const { wrapper } = mountPage(read)
    await flushPromises(); await vi.advanceTimersByTimeAsync(1000)
    for (const [index, seconds] of [1, 2, 4, 8, 15].entries()) {
      expect(wrapper.text()).toContain(seconds + ' 秒后重试')
      await vi.advanceTimersByTimeAsync(seconds * 1000 - 1)
      expect(read).toHaveBeenCalledTimes(index + 2)
      await vi.advanceTimersByTimeAsync(1)
    }
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(3)
  })
  it('缓冲和 DOM 始终最多 600 行，明确淘汰提示', async () => {
    vi.useFakeTimers()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page(1, 200, true)).mockResolvedValueOnce(page(201, 200, true)).mockResolvedValueOnce(page(401, 200, true)).mockResolvedValue(page(601, 200))
    const { wrapper } = mountPage(read)
    await flushPromises(); await vi.advanceTimersByTimeAsync(5)
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(600)
    expect(wrapper.findAll('[data-log-id]')[0]!.attributes('data-log-id')).toBe(epoch + ':201')
    expect(wrapper.text()).toContain('已淘汰 200 条')
  })
  it('滚动向上只暂停跟随，接收不停；未读数只计当前筛选', async () => {
    vi.useFakeTimers()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockResolvedValue(page(4, 2))
    const { wrapper } = mountPage(read)
    await flushPromises()
    const terminal = wrapper.get('[data-testid="logs-terminal"]')
    Object.defineProperties(terminal.element, { scrollHeight: { value: 1000 }, clientHeight: { value: 200 }, scrollTop: { value: 10, writable: true } })
    await terminal.trigger('scroll')
    await wrapper.get('[aria-label="日志级别"]').setValue('warn')
    await vi.advanceTimersByTimeAsync(1000)
    expect(read).toHaveBeenCalledTimes(2)
    const latest = wrapper.findAll('button').find(button => button.text().includes('返回最新'))!
    expect(latest.text()).toContain('1 条未读')
    await latest.trigger('click')
    expect(terminal.element.scrollTop).toBe(1000)
    expect(wrapper.text()).toContain('跟随最新')
  })
  it.each([1, 12, 24])('向上滚动仅 %i px 也立即暂停跟随，下一批不吸回底部', async distance => {
    vi.useFakeTimers()
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockResolvedValue(page(4, 2))
    const { wrapper } = mountPage(read)
    await flushPromises()
    const terminal = wrapper.get('[data-testid="logs-terminal"]')
    const position = 800 - distance
    Object.defineProperties(terminal.element, { scrollHeight: { value: 1000 }, clientHeight: { value: 200 }, scrollTop: { value: position, writable: true } })
    await terminal.trigger('scroll')
    expect(wrapper.text()).toContain('阅读中 · 接收不受影响')
    await vi.advanceTimersByTimeAsync(1000)
    expect(read).toHaveBeenCalledTimes(2)
    expect(terminal.element.scrollTop).toBe(position)
  })
  it('清空仅本地，保留游标和同序列重置清空水位', async () => {
    vi.useFakeTimers()
    const reset = { ...page(1, 4), resetReason: 'expired' as const, omittedCount: 1 }
    const read = vi.fn<FeedReader>().mockResolvedValueOnce(page()).mockResolvedValue(reset)
    const { wrapper } = mountPage(read)
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '清空本页')!.trigger('click')
    expect(read).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(1000)
    expect(read.mock.calls[1]?.[0]).toBe('cursor-3')
    expect(wrapper.findAll('[data-log-id]')).toHaveLength(1)
    expect(wrapper.text()).toContain('安全摘要 4')
    expect(wrapper.text()).toContain('遗漏 1 条')
  })
})
