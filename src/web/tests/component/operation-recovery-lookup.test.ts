import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient } from '../../src/api/zba'
import OperationRecoveryLookup from '../../src/features/overview/OperationRecoveryLookup.vue'

const owner = 'preset.feature.token-quota'
const receipt = { formatVersion: 1, owner, scopeDigest: 'a'.repeat(64), keyDigest: 'b'.repeat(64),
  requestDigest: 'c'.repeat(64), contract: 'quota.management', contractVersion: '1', generation: 'synthetic-generation',
  state: 'unknown', provenance: 'recorded' }
const found = { found: true, receipt, details: 'unavailable' }
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const mounted: Array<{ unmount(): void }> = []
afterEach(() => { for (const item of mounted.splice(0)) item.unmount() })
function setup(fetchImpl: typeof fetch, enabled = true) {
  const client = new ZbaClient({ token: 'synthetic-admin', fetchImpl })
  const wrapper = mount(OperationRecoveryLookup, { props: { client, enabled } })
  mounted.push(wrapper)
  return wrapper
}
async function find(wrapper: ReturnType<typeof setup>, key = 'synthetic-original-key', plugin = owner) {
  await wrapper.findAll('input')[0]!.setValue(plugin)
  await wrapper.findAll('input')[1]!.setValue(key)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}
describe('operation evidence recovery', () => {
  it.each(['synthetic-original-key', ' preserve original spaces ', 'a'.repeat(256)])('preserves the exact original key and offers no resubmit: %s', async key => {
    const fetchImpl = vi.fn<typeof fetch>().mockResolvedValue(response(found))
    const wrapper = setup(fetchImpl)
    await find(wrapper, key)
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/zba/v1/operations/find')
    expect(fetchImpl.mock.calls[0]?.[1]).toMatchObject({ method: 'POST', body: JSON.stringify({ owner, idempotencyKey: key }) })
    expect(wrapper.get('[role="status"]').text()).toContain('结果未知（Unknown）')
    expect(wrapper.text()).toContain('避免重复提交')
    expect(wrapper.findAll('button')).toHaveLength(1)
  })
  it.each(['short', 'a'.repeat(257), 'invalid/key', String.fromCharCode(92) + 'invalid-key', '中文synthetic'])('rejects invalid key %s', async key => {
    const fetchImpl = vi.fn<typeof fetch>()
    await find(setup(fetchImpl), key)
    expect(fetchImpl).not.toHaveBeenCalled()
  })
  it.each(['0plugin', 'plugin..invalid', 'plugin.', 'plugin_invalid'])('rejects invalid owner %s', async plugin => {
    const fetchImpl = vi.fn<typeof fetch>()
    await find(setup(fetchImpl), 'synthetic-original-key', plugin)
    expect(fetchImpl).not.toHaveBeenCalled()
  })
  it('keeps a missing record inconclusive', async () => {
    const wrapper = setup(async () => response({ found: false, receipt: null, details: 'unavailable' }))
    await find(wrapper)
    expect(wrapper.get('[role="status"]').text()).toContain('这不能证明操作未执行')
  })
  it('shows legacy evidence without inventing a generation or business details', async () => {
    const wrapper = setup(async () => response({ ...found, receipt: { ...receipt, generation: null, provenance: 'legacy', state: 'committed' } }))
    await find(wrapper)
    expect(wrapper.text()).toContain('已提交')
    expect(wrapper.text()).toContain('未记录当时的运行版本')
    expect(wrapper.text()).not.toContain(receipt.requestDigest)
  })
  it('gates read mode even on direct submit', async () => {
    const fetchImpl = vi.fn<typeof fetch>()
    await find(setup(fetchImpl, false))
    expect(fetchImpl).not.toHaveBeenCalled()
  })
  it.each([401, 403])('invalidates authentication for %s', async status => {
    const wrapper = setup(async () => response({ error: { code: 'synthetic_auth', message: 'synthetic' } }, status))
    await find(wrapper)
    expect(wrapper.emitted('auth-expired')?.[0]?.[0]).toMatchObject({ status })
  })
  it.each(['client', 'permission', 'key', 'owner', 'unmount'])('discards late evidence after %s changes', async change => {
    let finish: (value: Response) => void = () => { throw new Error('not started') }
    const fetchImpl = vi.fn<typeof fetch>().mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const wrapper = setup(fetchImpl)
    await find(wrapper)
    await wrapper.get('form').trigger('submit')
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    if (change === 'client') await wrapper.setProps({ client: new ZbaClient({ token: 'synthetic-other', fetchImpl }) })
    if (change === 'permission') await wrapper.setProps({ enabled: false })
    if (change === 'key') await wrapper.findAll('input')[1]!.setValue('synthetic-other-key')
    if (change === 'owner') await wrapper.findAll('input')[0]!.setValue('synthetic.other')
    if (change === 'unmount') mounted.pop()?.unmount()
    finish(response(found)); await flushPromises()
    if (change !== 'unmount') expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })
  it.each([
    { ...receipt, owner: 'synthetic.other' }, { ...receipt, formatVersion: 2 },
    { ...receipt, generation: null }, { ...receipt, provenance: 'legacy', generation: null },
    { ...receipt, requestDigest: 'invalid' }, { ...receipt, state: 'succeeded' }
  ])('refuses mismatched or invalid evidence %#', async invalid => {
    const wrapper = setup(async () => response({ ...found, receipt: invalid }))
    await find(wrapper)
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })
})
