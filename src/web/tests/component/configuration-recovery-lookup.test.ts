import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient } from '../../src/api/zba'
import ConfigurationRecoveryLookup from '../../src/features/configuration/ConfigurationRecoveryLookup.vue'
import fixture from '../fixtures/configuration-find-result.json'

const key = fixture.receipt.key
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const mounted: Array<{ unmount(): void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })
function setup(fetchImpl: typeof fetch, enabled = true, pending = false) {
  const client = new ZbaClient({ token: 'synthetic-admin', fetchImpl })
  const wrapper = mount(ConfigurationRecoveryLookup, { props: { client, enabled, pending } })
  mounted.push(wrapper)
  return { wrapper, client }
}
async function find(wrapper: ReturnType<typeof setup>['wrapper'], originalKey = key) {
  await wrapper.get('input').setValue(originalKey)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('manual configuration receipt lookup', () => {
  it('queries only the exact original key and labels the outcome as historical', async () => {
    const fetchImpl = vi.fn<typeof fetch>().mockResolvedValue(response(fixture))
    const { wrapper } = setup(fetchImpl)
    await find(wrapper)
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/zba/v1/configuration/find')
    expect(fetchImpl.mock.calls[0]?.[1]).toMatchObject({ method: 'POST', body: JSON.stringify({ key }) })
    const receipt = wrapper.get('[data-testid="config-recovery-receipt"]')
    expect(receipt.text()).toContain('该次提交的历史结果')
    expect(receipt.text()).toContain('历史回执不代表当前配置值和运行状态')
    expect(receipt.text()).toContain('不会清空草稿或解除其他未决操作')
    expect(receipt.text()).toContain('该次持久化版本8')
    expect(wrapper.emitted('auth-expired')).toBeUndefined()
  })

  it.each([2, 3, 4])('preserves persisted success separately from application state %s', async applyState => {
    const receipt = { ...fixture.receipt, applyState, steps: [{ step: 2, state: applyState, errorCode: 'synthetic_apply_failure' }] }
    const { wrapper } = setup(async () => response({ code: 0, receipt }))
    await find(wrapper)
    expect(wrapper.get('[data-testid="config-recovery-receipt"]').text()).toContain('配置已保存')
    expect(wrapper.text()).toContain('模型投影')
    expect(wrapper.text()).toContain('synthetic_apply_failure')
    expect(wrapper.findAll('button')).toHaveLength(1)
    if (applyState === 3) {
      expect(wrapper.text()).toContain('仅凭历史回执不能判断现在是否仍需重启')
      expect(wrapper.text()).not.toContain('请由运维重启')
    }
  })

  it('explains a historical no-change receipt without inventing a committed version', async () => {
    const receipt = { ...fixture.receipt, decision: 1, commitState: 0, committedVersion: undefined, applyState: 0 }
    const { wrapper } = setup(async () => response({ code: 0, receipt }))
    await find(wrapper)
    expect(wrapper.text()).toContain('没有需要保存的新修改')
    expect(wrapper.text()).toContain('该次持久化版本—')
  })

  it.each([
    [14, 404, '未找到'], [5, 410, '已过期'], [6, 409, '代际已变化'], [12, 503, '仍待核对']
  ])('does not turn result %s into a receipt or a save action', async (code, status, message) => {
    const fetchImpl = vi.fn<typeof fetch>().mockResolvedValue(response({ code }, Number(status)))
    const { wrapper } = setup(fetchImpl)
    await find(wrapper)
    expect(wrapper.get('[data-testid="config-recovery-code"]').text()).toContain(String(message))
    expect(wrapper.find('[data-testid="config-recovery-receipt"]').exists()).toBe(false)
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('button')).toHaveLength(1)
  })

  it.each([
    undefined,
    { ...fixture.receipt, key: key.replace('fedc', 'abcd') },
    { ...fixture.receipt, commitState: 2 },
    { ...fixture.receipt, applyState: 99 },
    { ...fixture.receipt, decision: 99 },
    { ...fixture.receipt, steps: [{ step: 0, state: 99 }] }
  ])('does not present an unverifiable success receipt', async receipt => {
    const { wrapper } = setup(async () => response({ code: 0, receipt }))
    await find(wrapper)
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="config-recovery-receipt"]').exists()).toBe(false)
    expect(wrapper.get('input').element.value).toBe(key)
  })

  it.each([[fixture, 503], [{ code: 14 }, 200]])('rejects contradictory HTTP and business outcomes', async (body, status) => {
    const { wrapper } = setup(async () => response(body, Number(status)))
    await find(wrapper)
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="config-recovery-code"]').exists()).toBe(false)
  })

  it.each([[{ code: 1 }, 403], [{ error: { code: 'synthetic_auth', message: 'synthetic' } }, 401]])('forwards authentication failures', async (body, status) => {
    const { wrapper } = setup(async () => response(body, Number(status)))
    await find(wrapper)
    expect(wrapper.emitted('auth-expired')?.[0]?.[0]).toMatchObject({ status })
  })

  it.each([[false, false], [true, true]])('does not query in read mode or while another save is unresolved', async (enabled, pending) => {
    const fetchImpl = vi.fn<typeof fetch>()
    const { wrapper } = setup(fetchImpl, enabled, pending)
    await find(wrapper)
    expect(fetchImpl).not.toHaveBeenCalled()
  })

  it.each(['client', 'permission', 'pending', 'key', 'unmount'])('ignores a late response after %s changes', async change => {
    let finish: (value: Response) => void = () => { throw new Error('request not started') }
    const fetchImpl = vi.fn<typeof fetch>().mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const { wrapper } = setup(fetchImpl)
    await wrapper.get('input').setValue(key)
    await wrapper.get('form').trigger('submit')
    await wrapper.get('form').trigger('submit')
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    if (change === 'client') await wrapper.setProps({ client: new ZbaClient({ token: 'synthetic-other', fetchImpl }) })
    if (change === 'permission') await wrapper.setProps({ enabled: false })
    if (change === 'pending') await wrapper.setProps({ pending: true })
    if (change === 'key') await wrapper.get('input').setValue(key.replace('fedc', 'abcd'))
    if (change === 'unmount') mounted.pop()?.unmount()
    finish(response(fixture))
    await flushPromises()
    expect(wrapper.find('[data-testid="config-recovery-receipt"]').exists()).toBe(false)
    expect(wrapper.emitted('auth-expired')).toBeUndefined()
  })

  it.each(['', 'synthetic-task-key', key.toUpperCase(), ' ' + key, key + 'x',
    key.replace('1760000000', '01760000000'), key.replace('1760000000', '9223372036854775807'),
    key.replace('0123456789abcdef0123456789abcdef', '0'.repeat(32))])('keeps invalid input out of requests', async invalid => {
    const fetchImpl = vi.fn<typeof fetch>()
    const { wrapper } = setup(fetchImpl)
    await find(wrapper, invalid)
    expect(fetchImpl).not.toHaveBeenCalled()
  })
})
