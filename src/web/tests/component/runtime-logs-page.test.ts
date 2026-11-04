import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import RuntimeLogsPage from '../../src/features/logs/RuntimeLogsPage.vue'
import { ZbaRequestError, type ZbaClient, type ZbaRuntimeLogPage } from '../../src/api/zba'

const mounted: Array<{ unmount(): void }> = []
afterEach(() => { mounted.splice(0).forEach(w => w.unmount()); vi.useRealTimers(); vi.restoreAllMocks() })
const sample: ZbaRuntimeLogPage = { capacity: 600, items: [
  { time: 1793773800000, level: 'warn', source: 'Agent', message: '模型输出为空（empty_reply）', redacted: true },
  { time: 1793773790000, level: 'info', source: 'Web', message: '服务已启动', redacted: true },
  { time: 1793773780000, level: 'error', source: 'Other', message: '内容已隐藏（长度 88）', redacted: true }
] }
function client(read: () => Promise<ZbaRuntimeLogPage>): ZbaClient { return { listRuntimeLogs: read } as unknown as ZbaClient }
function setup(read = vi.fn(async () => sample)) {
  const wrapper = mount(RuntimeLogsPage, { props: { client: client(read), active: true } })
  mounted.push(wrapper)
  return { wrapper, read }
}
function deferred() {
  let resolve!: (page: ZbaRuntimeLogPage) => void
  const promise = new Promise<ZbaRuntimeLogPage>(r => { resolve = r })
  return { promise, resolve }
}

describe('运行日志页', () => {
  it('默认手动刷新，按级别、来源、关键词筛选当前快照', async () => {
    vi.useFakeTimers()
    const { wrapper, read } = setup()
    await flushPromises()
    expect(wrapper.findAll('.log-row')).toHaveLength(3)
    expect(wrapper.text()).toContain('聊天正文、成员信息和凭据不会返回浏览器')
    expect(wrapper.find('button').text()).toBe('刷新日志')
    await vi.advanceTimersByTimeAsync(15000)
    expect(read).toHaveBeenCalledTimes(1)
    const selects = wrapper.findAll('select')
    await selects[0]!.setValue('warn')
    expect(wrapper.findAll('.log-row')).toHaveLength(1)
    await selects[1]!.setValue('Web')
    expect(wrapper.text()).toContain('没有匹配的日志')
    await wrapper.findAll('button').find(b => b.text() === '清除筛选')!.trigger('click')
    await wrapper.get('input[type=search]').setValue('EMPTY_REPLY')
    expect(wrapper.findAll('.log-row')).toHaveLength(1)
    expect(wrapper.find('.log-row').text()).toContain('Agent')
    expect(read).toHaveBeenCalledTimes(1)
  })

  it('空态与错误态明确区分；失败保留旧快照但暂停自动刷新', async () => {
    const read = vi.fn(async () => ({ capacity: 600, items: [] } as ZbaRuntimeLogPage))
    const { wrapper } = setup(read)
    await flushPromises()
    expect(wrapper.text()).toContain('暂无运行日志')
    read.mockResolvedValueOnce(sample)
    await wrapper.get('button').trigger('click'); await flushPromises()
    read.mockRejectedValueOnce(new ZbaRequestError(503, 'unavailable', 'unsafe response text'))
    await wrapper.get('input[type=checkbox]').setValue(true)
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(wrapper.findAll('.log-row')).toHaveLength(3)
    expect(wrapper.text()).toContain('保留上次成功的快照')
    expect(wrapper.text()).not.toContain('unsafe response text')
    expect((wrapper.get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(false)
  })

  it('5秒轮询不重叠，离开或隐藏页面暂停，返回再读取', async () => {
    vi.useFakeTimers()
    let visibility = 'visible'
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility as DocumentVisibilityState)
    const { wrapper, read } = setup()
    await flushPromises()
    await wrapper.get('input[type=checkbox]').setValue(true)
    const pending = deferred()
    read.mockImplementationOnce(() => pending.promise)
    await vi.advanceTimersByTimeAsync(5000)
    await vi.advanceTimersByTimeAsync(20000)
    expect(read).toHaveBeenCalledTimes(2)
    pending.resolve(sample); await flushPromises()
    await wrapper.setProps({ active: false })
    await vi.advanceTimersByTimeAsync(10000)
    expect(read).toHaveBeenCalledTimes(2)
    await wrapper.setProps({ active: true }); await flushPromises()
    expect(read).toHaveBeenCalledTimes(3)
    visibility = 'hidden'; document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(10000)
    expect(read).toHaveBeenCalledTimes(3)
    visibility = 'visible'; document.dispatchEvent(new Event('visibilitychange')); await flushPromises()
    expect(read).toHaveBeenCalledTimes(4)
  })

  it('新身份立即清空缓存，旧请求不能回填，新请求优先', async () => {
    const old = deferred()
    const { wrapper } = setup(vi.fn(() => old.promise))
    const next = deferred()
    await wrapper.setProps({ client: client(() => next.promise) })
    old.resolve(sample); await flushPromises()
    expect(wrapper.findAll('.log-row')).toHaveLength(0)
    next.resolve({ capacity: 600, items: [] }); await flushPromises()
    expect(wrapper.text()).toContain('暂无运行日志')
    expect(wrapper.text()).not.toContain('empty_reply')
  })

  it('较晚返回的旧读取不覆盖条数变更后的新快照', async () => {
    const old = deferred()
    const read = vi.fn(() => old.promise)
    const { wrapper } = setup(read)
    read.mockResolvedValueOnce({ capacity: 600, items: [] })
    await wrapper.findAll('select')[2]!.setValue('600'); await flushPromises()
    old.resolve(sample); await flushPromises()
    expect(wrapper.findAll('.log-row')).toHaveLength(0)
    expect(wrapper.text()).toContain('暂无运行日志')
  })

  it.each([401, 403])('HTTP %i 清空旧快照并停轮询，401 通知会话失效', async status => {
    const { wrapper, read } = setup()
    await flushPromises()
    read.mockRejectedValueOnce(new ZbaRequestError(status, 'forbidden', 'unsafe'))
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(wrapper.findAll('.log-row')).toHaveLength(0)
    expect(wrapper.emitted('auth-expired')?.length ?? 0).toBe(status === 401 ? 1 : 0)
  })

  it('模板按文本渲染摘要，不解析 HTML', async () => {
    const { wrapper } = setup(vi.fn(async () => ({ capacity: 600, items: [{ ...sample.items[0]!, message: '<img src=x onerror=alert(1)>' }] })))
    await flushPromises()
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
  })
})
