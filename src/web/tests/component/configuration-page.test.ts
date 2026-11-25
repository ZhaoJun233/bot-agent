import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ConfigurationPage from '../../src/features/configuration/ConfigurationPage.vue'
import {
  ZbaClient, ZbaNetworkError, ZbaRequestError,
  type ZbaApplyState, type ZbaConfigurationReadResult, type ZbaConfigurationReceipt, type ZbaConfigurationSaveRequest
} from '../../src/api/zba'
import editableFixture from '../fixtures/configuration-read-editable-result.json'
import { getPendingOperation } from '../../src/shared/sync/pending-operations'
import { registerPlugin } from '../../src/features/capabilities/plugin-settings'

const mounted: Array<{ unmount: () => void }> = []
const disposers: Array<() => void> = []
let clientIdentity = 0

afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
  for (const dispose of disposers.splice(0)) dispose()
})

function registerSyntheticProviderScope(): string {
  const key = 'synthetic-providers'
  disposers.push(registerPlugin({
    pluginKey: key,
    kind: 'system',
    title: '合成供应商编辑能力',
    summary: '测试通用配置页供应商改名路径',
    fields: [{ section: 'model', key: 'fastModel' }],
    providers: true
  }))
  return key
}

function agentSecretFixture(): ZbaConfigurationReadResult {
  const copy = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
  copy.secrets[1] = { slot: 1, configured: true, source: 1 }
  return copy
}

function clientWith(
  result: unknown,
  overrides: Partial<Pick<ZbaClient, 'readConfiguration' | 'saveConfiguration' | 'validateConfiguration' | 'findConfiguration'>> = {}
): ZbaClient {
  const identity = ++clientIdentity
  return {
    baseUrl: '/synthetic-configuration-existing-' + identity,
    identityLabel: 'configuration-existing-' + identity,
    readConfiguration: async () => result,
    ...overrides,
    ...(overrides.saveConfiguration ? { saveConfiguration: async (request: ZbaConfigurationSaveRequest) => {
      const result = await overrides.saveConfiguration!(request)
      return result.receipt?.key === receipt.key ? { ...result, receipt: { ...result.receipt, key: request.key } } : result
    } } : {}),
    ...(overrides.findConfiguration ? { findConfiguration: async (key: string) => {
      const result = await overrides.findConfiguration!(key)
      return result.receipt?.key === receipt.key ? { ...result, receipt: { ...result.receipt, key } } : result
    } } : {})
  } as unknown as ZbaClient
}

const receipt: ZbaConfigurationReceipt = {
  key: 'cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210',
  contractRevision: 'cfg-0.3.3',
  decision: 0,
  observedVersion: '1',
  committedVersion: '2',
  commitState: 1,
  applyState: 1,
  steps: [{ step: 0, state: 1 }],
  deviceKeyAssignments: []
}

function mountPage(overrides: Partial<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  scope: string
  active: boolean
}> = {}) {
  const wrapper = mount(ConfigurationPage, {
    props: {
      client: clientWith(editableFixture),
      mode: 'read',
      writesEnabled: true,
      ...overrides
    }
  })
  mounted.push(wrapper)
  return wrapper
}

function providerSnapshot(nameRaw = 'Synthetic Provider A', version = '1'): ZbaConfigurationReadResult {
  const snapshot = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
  const provider = snapshot.view?.registered.providers[0]
  if (!provider) throw new Error('Synthetic fixture must include a provider')
  snapshot.version = version
  if (snapshot.view?.model.model) {
    snapshot.view.model.fastModel = {
      ...structuredClone(snapshot.view.model.model),
      value: 'sample-fast-model',
      rule: { ...snapshot.view.model.model.rule, id: 50 }
    }
  }
  provider.name.visibility = 2
  provider.name.nameRaw = nameRaw
  delete provider.name.value
  return snapshot
}

function providerInput(wrapper: ReturnType<typeof mountPage>) {
  return wrapper.get<HTMLInputElement>('[data-testid="config-provider-name-provider-sample"]')
}

async function renameProvider(wrapper: ReturnType<typeof mountPage>, nameRaw: string): Promise<void> {
  await wrapper.get('[data-testid="config-nav-registered"]').trigger('click')
  await providerInput(wrapper).setValue(nameRaw)
}

async function saveDraft(wrapper: ReturnType<typeof mountPage>): Promise<void> {
  await wrapper.get('[data-testid="config-save"]').trigger('click')
  await flushPromises()
  await flushPromises()
}

function committedReceipt(request: ZbaConfigurationSaveRequest, version: string): ZbaConfigurationReceipt {
  return { ...receipt, key: request.key, observedVersion: request.expectedVersion, committedVersion: version }
}

async function expectCleanProviderDraft(wrapper: ReturnType<typeof mountPage>, nameRaw: string): Promise<void> {
  expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  expect(wrapper.get('[data-testid="config-providers"]').text()).not.toContain('已改名')
  expect(providerInput(wrapper).element.value).toBe(nameRaw)
  expect(wrapper.find('[data-testid="config-check-pending"]').exists()).toBe(false)
  const unload = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(unload)
  expect(unload.defaultPrevented).toBe(false)
  const page = wrapper.vm as unknown as { requestLeave: (reason: 'lock') => Promise<boolean> }
  const leaving = page.requestLeave('lock')
  await flushPromises()
  // Assert before awaiting so a leaked draft fails rather than hanging on the dialog.
  expect(document.body.querySelector('[data-testid="config-leave-dialog"]')).toBeNull()
  await expect(leaving).resolves.toBe(true)
}

describe('ConfigurationPage runtime and explicit denial', () => {
  it('a baseline reread cannot swallow an explicit discard or restore the discarded draft', async () => {
    let release!: (snapshot: ZbaConfigurationReadResult) => void
    let holdRead = false
    let writes = 0
    const wrapper = mountPage({ mode: 'admin', scope: 'agent', client: clientWith(editableFixture, {
      readConfiguration: () => holdRead
        ? new Promise<ZbaConfigurationReadResult>(resolve => { release = resolve })
        : Promise.resolve(editableFixture as unknown as ZbaConfigurationReadResult),
      saveConfiguration: async () => { writes += 1; throw new Error('Discard must not write') }
    }) })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-agentPrompt"] textarea').setValue('Synthetic discarded prompt')
    holdRead = true
    const reread = (wrapper.vm as unknown as { reload: () => Promise<boolean> }).reload()
    await flushPromises()
    try {
      const discard = wrapper.get<HTMLButtonElement>('[data-testid="config-discard"]').element
      expect(discard.disabled).toBe(false)
      discard.click()
      await flushPromises()
      expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
      expect(writes).toBe(0)
    } finally {
      release(editableFixture as unknown as ZbaConfigurationReadResult)
      await reread
    }
    await flushPromises()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('a later successful receipt cannot clear unrelated server-reported restart debt', async () => {
    const data = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
    data.latestApplyState = 1
    data.runtimeEffects = [{ field: { field: data.view!.behavior.botPersona!.rule.id, object: null }, state: 'restartRequired', restartTarget: 'botAgent' }]
    const client = clientWith(data, { saveConfiguration: async () => ({ code: 0, replayed: false, receipt: { ...receipt, applyState: 1 }, currentVersion: '2', errors: [] }) })
    const wrapper = mountPage({ client, mode: 'admin', scope: 'basic' })
    await flushPromises()
    expect(wrapper.get('[data-testid="config-runtime-restart"]').text()).toContain('BotAgent')
    expect(wrapper.get('[data-testid="config-field-botPersona"]').text()).toContain('需重启')
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic new draft')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="config-receipt-outcome"]').text()).toContain('本次修改已应用')
    expect(wrapper.get('[data-testid="config-runtime-restart"]').text()).toContain('BotAgent')
  })
  it('missing runtime metadata does not reuse latest successful apply state', async () => {
    const data = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
    data.latestApplyState = 1
    delete data.runtimeEffects
    const wrapper = mountPage({ client: clientWith(data), mode: 'read', scope: 'basic' })
    await flushPromises()
    expect(wrapper.get('[data-testid="config-field-botPersona"]').text()).toContain('当前生效情况待确认')
  })
  it('explicit denial requires confirmation and another draft edit resets it', async () => {
    const calls: ZbaConfigurationSaveRequest[] = []
    const client = clientWith(editableFixture, { saveConfiguration: async request => { calls.push(request); return { code: 0, replayed: false, receipt, currentVersion: '2', errors: [] } } })
    const wrapper = mountPage({ client, mode: 'admin', scope: 'platform' })
    await flushPromises()
    await wrapper.get('[data-testid="config-nav-platform"]').trigger('click')
    const group = wrapper.get('[data-testid^="policy-group-"][data-testid$="-kind"]')
    await group.setValue('3')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(calls).toHaveLength(0)
    const confirm = wrapper.get('[data-testid="config-policy-denials-confirm"]')
    await confirm.setValue(true)
    await group.setValue('1')
    expect((confirm.element as HTMLInputElement).checked).toBe(false)
    await confirm.setValue(true)
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(calls).toHaveLength(1)
    expect(calls[0]!.changes.registered!.policies[0]!.configuration.groupWhitelist).toEqual({ kind: 1, value: '' })
  })
})

describe('ConfigurationPage', () => {
  it.each(['replace', 'delete'] as const)('F6 keeps ordinary drafts and clears the %s secret intent without saving', async secretMode => {
    const save = vi.fn()
    const wrapper = mountPage({ mode: 'admin', scope: 'agent', client: clientWith(agentSecretFixture(), { saveConfiguration: save }) })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-agentPrompt"] textarea').setValue('Synthetic retained prompt')
    await wrapper.get('[data-testid="config-secrets"] [data-testid="secret-' + secretMode + '"]').trigger('click')
    if (secretMode === 'replace') await wrapper.get('[data-testid="config-secrets"] input').setValue('synthetic-private-replacement')
    expect(wrapper.vm.draftCount).toBe(2)
    const leaving = wrapper.vm.requestLeave('navigate')
    await flushPromises()
    const dialog = document.querySelector('[data-testid="config-leave-dialog"]')!
    const keep = dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-keep"]')
    expect(keep).not.toBeNull()
    expect(keep!.textContent).toContain('保留普通草稿并离开')
    expect(dialog.textContent).toContain('秘密替换输入')
    expect(dialog.textContent).toContain('秘密修改意图')
    expect(dialog.querySelector('[data-testid="config-leave-stay"]')).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-leave-save"]')).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-leave-discard"]')).not.toBeNull()
    keep!.click()
    await expect(leaving).resolves.toBe(true)
    await flushPromises()
    expect(save).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(false)
    expect(wrapper.vm.draftCount).toBe(1)
    expect(wrapper.find('[data-testid="config-secrets"] input').exists()).toBe(false)
    await wrapper.setProps({ active: false })
    await wrapper.setProps({ active: true })
    await flushPromises()
    expect(wrapper.get<HTMLTextAreaElement>('[data-testid="config-field-agentPrompt"] textarea').element.value).toBe('Synthetic retained prompt')
    expect(wrapper.vm.draftCount).toBe(1)
    expect(save).not.toHaveBeenCalled()
  })

  it.each(['close', 'lock', 'downgrade'] as const)('F6 never offers keep for %s', async reason => {
    const wrapper = mountPage({ mode: 'admin' })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic non-navigation draft')
    const leaving = wrapper.vm.requestLeave(reason)
    await flushPromises()
    expect(document.querySelector('[data-testid="config-leave-keep"]')).toBeNull()
    if (reason === 'lock') await expect(leaving).resolves.toBe(true)
    else {
      document.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')!.click()
      await expect(leaving).resolves.toBe(false)
    }
  })

  it.each(['saving', 'unknown'] as const)('F6 cannot keep while the save is %s', async state => {
    type Result = Awaited<ReturnType<ZbaClient['saveConfiguration']>>
    let finish!: (result: Result) => void
    let request: ZbaConfigurationSaveRequest | undefined
    const save = vi.fn((value: ZbaConfigurationSaveRequest): Promise<Result> => {
      request = value
      if (state === 'unknown') return Promise.reject(new ZbaNetworkError(new Error('Synthetic interrupted save')))
      return new Promise(resolve => { finish = resolve })
    })
    const wrapper = mountPage({ mode: 'admin', client: clientWith(editableFixture, { saveConfiguration: save }) })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic in-flight draft')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    const visibleKey = wrapper.get<HTMLInputElement>('[data-testid="configuration-operation-key"]')
    expect(visibleKey.element.value).toBe(request?.key)
    expect(visibleKey.element.readOnly).toBe(true)
    const leaving = wrapper.vm.requestLeave('navigate')
    await flushPromises()
    const dialog = document.querySelector('[data-testid="config-leave-dialog"]')!
    expect(dialog).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-leave-keep"]')).toBeNull()
    expect(dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')!.disabled).toBe(true)
    expect(dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-discard"]')!.disabled).toBe(true)
    expect(dialog.querySelector('[data-testid="config-leave-' + (state === 'saving' ? 'saving' : 'unconfirmed') + '"]')).not.toBeNull()
    dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')!.click()
    await expect(leaving).resolves.toBe(false)
    expect(save).toHaveBeenCalledTimes(1)
    if (state === 'saving') {
      if (!request) throw new Error('Synthetic save did not start')
      finish({ code: 0, replayed: false, receipt: { ...receipt, key: request.key, observedVersion: request.expectedVersion }, currentVersion: '2', errors: [] })
      await flushPromises()
      expect(wrapper.find('[data-testid="configuration-operation-key"]').exists()).toBe(false)
    }
  })

  it('shows the pending operation key only to its active authorized owner', async () => {
    const save = vi.fn(async () => { throw new ZbaNetworkError() })
    const client = clientWith(editableFixture, { saveConfiguration: save })
    const wrapper = mountPage({ mode: 'admin', client })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic pending draft')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    const key = wrapper.get<HTMLInputElement>('[data-testid="configuration-operation-key"]').element.value
    for (const boundary of [{ active: false }, { mode: 'read' as const }, { writesEnabled: false }, { client: clientWith(editableFixture) }]) {
      await wrapper.setProps(boundary)
      await flushPromises()
      expect(wrapper.find('[data-testid="configuration-operation-key"]').exists()).toBe(false)
      await wrapper.setProps({ active: true, mode: 'admin', writesEnabled: true, client })
      await flushPromises()
      expect(wrapper.get<HTMLInputElement>('[data-testid="configuration-operation-key"]').element.value).toBe(key)
    }
    expect(save).toHaveBeenCalledTimes(1)
  })

  it('F6 does not expose keep when write permission is disabled', async () => {
    const wrapper = mountPage({ mode: 'admin' })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic read-only draft')
    await wrapper.setProps({ writesEnabled: false })
    const leaving = wrapper.vm.requestLeave('navigate')
    await flushPromises()
    const dialog = document.querySelector('[data-testid="config-leave-dialog"]')!
    expect(dialog).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-leave-keep"]')).toBeNull()
    dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')!.click()
    await expect(leaving).resolves.toBe(false)
  })

  it('ordinary dirty navigation asks before leaving and keeps the draft when staying', async () => {
    const save = vi.fn()
    const wrapper = mountPage({ mode: 'admin', client: clientWith(editableFixture, { saveConfiguration: save }) })
    await flushPromises()
    const input = wrapper.get<HTMLTextAreaElement>('[data-testid="config-field-botPersona"] textarea')
    await input.setValue('Synthetic unsaved navigation draft')
    const leaving = wrapper.vm.requestLeave('navigate')
    await flushPromises()
    const dialog = document.querySelector<HTMLElement>('[data-testid="config-leave-dialog"]')
    expect(dialog).not.toBeNull()
    if (!dialog) throw new Error('Expected navigation confirmation for an ordinary draft')
    const stay = dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')!
    const saveButton = dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')!
    const discard = dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-discard"]')!
    expect(stay.textContent?.trim()).toBe('继续编辑')
    expect(saveButton.textContent?.trim()).toBe('保存并离开当前页面')
    expect(discard.textContent?.trim()).toBe('放弃并离开当前页面')
    expect(saveButton.disabled).toBe(false)
    expect(discard.disabled).toBe(false)
    stay.click()
    await expect(leaving).resolves.toBe(false)
    await flushPromises()
    expect(document.querySelector('[data-testid="config-leave-dialog"]')).toBeNull()
    expect(save).not.toHaveBeenCalled()
    expect(wrapper.vm.draftCount).toBe(1)
    expect(input.element.value).toBe('Synthetic unsaved navigation draft')
  })

  it('待核对时切页使用当前页面文案，继续编辑留在当前页面', async () => {
    const save = vi.fn(async () => { throw new ZbaNetworkError(new Error('Synthetic save timeout')) })
    const wrapper = mountPage({ mode: 'admin', client: clientWith(editableFixture, { saveConfiguration: save }) })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic navigation draft')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    const leaving = wrapper.vm.requestLeave('navigate')
    await flushPromises()
    const dialog = document.querySelector('[data-testid="config-leave-dialog"]')!
    expect(dialog.querySelector('h2')?.textContent).toBe('离开当前页面前需要确认')
    expect(dialog.querySelector('.dialog-copy')?.textContent).toContain('离开前，请保存或明确放弃未保存的修改；选择继续编辑会留在当前页面。秘密输入仍按安全规则清理。')
    expect(dialog.querySelector('[data-testid="config-leave-save"]')?.textContent?.trim()).toBe('保存并离开当前页面')
    expect(dialog.querySelector('[data-testid="config-leave-discard"]')?.textContent?.trim()).toBe('放弃并离开当前页面')
    expect(dialog.textContent).not.toContain('退出管理员')
    expect(dialog.textContent).not.toContain('草稿会保留')
    dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')!.click()
    await expect(leaving).resolves.toBe(false)
    expect(save).toHaveBeenCalledTimes(1)
    expect(wrapper.vm.draftCount).toBe(1)
  })

  it.each([
    ['basic', 'lock'], ['basic', 'downgrade'], ['agent', 'lock'], ['agent', 'downgrade']
  ] as const)('隐藏的 %s 草稿只能通过明确的 %s 确认保存', async (scope, reason) => {
    const requests: ZbaConfigurationSaveRequest[] = []
    const wrapper = mountPage({ mode: 'admin', scope, client: clientWith(editableFixture, {
      saveConfiguration: async request => { requests.push(request); return { code: 0, replayed: false, currentVersion: '2', errors: [], receipt } }
    }) })
    await flushPromises()
    const key = scope === 'basic' ? 'botPersona' : 'agentPrompt'
    await wrapper.get('[data-testid="config-field-' + key + '"] input, [data-testid="config-field-' + key + '"] textarea').setValue('Synthetic retained draft')
    await wrapper.setProps({ active: false }); await flushPromises()
    // 隐藏普通保存入口（包括直接分派事件）不能绕过写保护。
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    expect(requests).toHaveLength(0)
    const leaving = wrapper.vm.requestLeave(reason); await flushPromises()
    if (reason === 'lock') {
      await expect(leaving).resolves.toBe(true)
      expect(document.querySelector('[data-testid="config-leave-dialog"]')).toBeNull()
      wrapper.vm.endSession()
      await flushPromises()
      expect(wrapper.vm.draftCount).toBe(0)
      expect(wrapper.find('[data-testid="config-secrets"] input').exists()).toBe(false)
      expect(requests).toHaveLength(0)
      return
    }
    const dialog = document.querySelector('[data-testid="config-leave-dialog"]')!
    expect(dialog.textContent).not.toContain('标准模式下不能保存')
    const saveButton = dialog.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')!
    expect(saveButton.disabled).toBe(false)
    expect(requests).toHaveLength(0)
    saveButton.click(); await flushPromises()
    expect(requests).toHaveLength(1)
    expect(requests[0]!.changes).toEqual({ [scope === 'basic' ? 'behavior' : 'agent']: { [key]: { kind: 1, value: 'Synthetic retained draft' } } })
    await expect(leaving).resolves.toBe(true)
  })

  it.each(['mode', 'writesEnabled'] as const)('离开确认期间 %s 权限失效仍不能保存隐藏草稿', async permission => {
    let writes = 0
    const wrapper = mountPage({ mode: 'admin', client: clientWith(editableFixture, {
      saveConfiguration: async () => { writes += 1; return { code: 0, errors: [], replayed: false, receipt, currentVersion: '2' } }
    }) })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('Synthetic draft')
    await wrapper.setProps({ active: false })
    const leaving = wrapper.vm.requestLeave('downgrade'); await flushPromises()
    await wrapper.setProps(permission === 'mode' ? { mode: 'read' } : { writesEnabled: false })
    const button = document.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')!
    expect(button.disabled).toBe(true)
    button.dispatchEvent(new MouseEvent('click', { bubbles: true })); await flushPromises()
    expect(writes).toBe(0)
    document.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')!.click()
    await expect(leaving).resolves.toBe(false)
  })
  it('基础配置只展示必要字段，不含其它能力和秘密入口', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('[data-testid="configuration"]').exists()).toBe(true)
    expect(wrapper.findAll('.config-nav-item')).toHaveLength(0)
    expect(wrapper.find('[data-testid="config-nav-voice"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="config-nav-music"]').exists()).toBe(false)
    // moodTtlSeconds 归「氛围与心情」插件，配置页不再重复展示
    expect(wrapper.findAll('[data-testid="config-fields"] tbody tr')).toHaveLength(1)
    expect(wrapper.find('[data-testid="config-field-moodTtlSeconds"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="config-field-botPersona"]').text()).toContain('示例人格')
    expect(wrapper.find('[data-testid="config-secrets"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('版本')
    expect(wrapper.find('[data-testid="config-readonly"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="config-owner-index"]').exists()).toBe(true)
    await wrapper.get('[data-testid="config-owner-models"]').trigger('click')
    expect(wrapper.emitted('open-capabilities')).toEqual([['models']])
  })

  it('模型路由能力仅展示路由与采样参数，不再混入主模型地址、密钥槽位或旧供应商表', async () => {
    const withFastModel = providerSnapshot()
    const wrapper = mountPage({ scope: 'models', client: clientWith(withFastModel) })
    await flushPromises()

    expect(wrapper.get('[data-testid="config-field-fastModel"]').text()).toContain('sample-fast-model')
    expect(wrapper.find('[data-testid="config-field-model"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="config-field-modelBaseUrl"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="config-secrets"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="config-nav-registered"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="config-providers"]').exists()).toBe(false)
  })

  it('标准模式提供管理员入口，管理员模式隐藏只读提示', async () => {
    const read = mountPage()
    await flushPromises()
    await read.get('[data-testid="config-enable-admin"]').trigger('click')
    expect(read.emitted('enable-admin')).toHaveLength(1)

    const admin = mountPage({ mode: 'admin' })
    await flushPromises()
    expect(admin.find('[data-testid="config-readonly"]').exists()).toBe(false)
    expect(admin.get('[data-testid="config-mode-badge"]').text()).toBe('管理员模式')
  })

  it('读取失败时显示安全错误，不渲染快照', async () => {
    const failing = {
      readConfiguration: async () => {
        throw new ZbaRequestError(403, 'forbidden', '该操作需要管理员凭据。')
      }
    } as unknown as ZbaClient

    const wrapper = mountPage({ client: failing })
    await flushPromises()

    expect(wrapper.text()).toContain('读取配置失败')
    expect(wrapper.text()).toContain('当前令牌没有该操作权限。')
    expect(wrapper.find('[data-testid="config-fields"]').exists()).toBe(false)
  })

  it('管理员编辑并保存：请求带 expectedVersion/操作键/patch，成功后清空草稿并显示回执', async () => {
    const calls: unknown[] = []
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => {
        calls.push(request)
        return { code: 0, replayed: false, receipt, currentVersion: '2', errors: [] }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('新人格')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(calls).toHaveLength(1)
    const request = calls[0] as {
      expectedVersion: string
      key: string
      changes: Record<string, Record<string, unknown>>
      secrets: unknown[]
    }
    expect(request.expectedVersion).toBe('1')
    expect(request.key).toMatch(/^cfg1:[0-9a-f]{32}:\d+:[0-9a-f]{32}$/)
    expect(request.changes.behavior?.botPersona).toEqual({ kind: 1, value: '新人格' })
    expect(request.secrets).toEqual([])
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('409 冲突保留草稿并显示冲突提示', async () => {
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => ({ code: 3, replayed: false, currentVersion: '2', errors: [] })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('冲突值')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)
    expect((wrapper.get('[data-testid="config-field-botPersona"] textarea').element as HTMLTextAreaElement).value).toBe('冲突值')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
  })

  it('起草后基线被重读：保存仍带起草时的 expectedVersion，不静默覆盖对方', async () => {
    let current = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
    current.version = '1'
    const versions: string[] = []
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => {
        versions.push(request.expectedVersion)
        return { code: 3, replayed: false, currentVersion: '2', errors: [] }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('我的人格')
    // 对方在此期间保存了同一字段，本页因切页/补读重读到 version 2。
    const other = structuredClone(current)
    other.version = '2'
    other.view!.behavior.botPersona = { ...other.view!.behavior.botPersona!, value: '对方的人格' }
    current = other
    await (wrapper.vm as unknown as { reload: () => Promise<boolean> }).reload()
    await flushPromises()

    await saveDraft(wrapper)
    expect(versions).toEqual(['1'])
    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)

    await wrapper.get('[data-testid="config-reload-conflict"]').trigger('click')
    await flushPromises()
    await flushPromises()
    // 只报对方也改过的字段；用户草稿保留。
    expect(wrapper.get('[data-testid="config-field-botPersona"]').text()).toContain('与对方修改重叠')
    expect((wrapper.get('[data-testid="config-field-botPersona"] textarea').element as HTMLTextAreaElement).value).toBe('我的人格')

    // 读取最新后改基：再次保存带新版本。
    await saveDraft(wrapper)
    expect(versions).toEqual(['1', '2'])
  })

  it.each([503, 408, 409, 429])('HTTP %s 保存结果未知时保留原操作键并允许核对回执', async (status) => {
    const keys: string[] = []
    const findKeys: string[] = []
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => {
        keys.push(request.key)
        throw new ZbaRequestError(status, 'http_error', '合成代理在上游提交后替换响应。')
      },
      findConfiguration: async (key) => {
        findKeys.push(key)
        return { code: 0, receipt: { ...receipt, key } }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('合成 503 待核对')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(findKeys).toEqual(keys)
    expect(findKeys).toHaveLength(1)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('网络中断进入待核对，核对成功显示回执', async () => {
    let finds = 0
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => {
        throw new ZbaNetworkError(new Error('timeout'))
      },
      findConfiguration: async () => {
        finds += 1
        return { code: 0, receipt }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('待核对')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(finds).toBe(1)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('待核对期间锁定编辑，NotFound 后恢复并用新操作键保存', async () => {
    const keys: string[] = []
    let saves = 0
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => {
        saves += 1
        keys.push(request.key)
        if (saves === 1) throw new ZbaNetworkError(new Error('offline'))
        return { code: 0, replayed: false, receipt, currentVersion: '2', errors: [] }
      },
      findConfiguration: async () => ({ code: 14 })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('未知结果')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    const input = wrapper.get('[data-testid="config-field-botPersona"] textarea')
    expect(input.attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="config-save"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="config-discard"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="config-validation"]').text()).toContain('没有找到这次提交的回执')
    expect(input.attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[data-testid="config-save"]').attributes('disabled')).toBeUndefined()

    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(keys).toHaveLength(2)
    expect(keys[0]).not.toBe(keys[1])
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('操作键过期（find 返回 5）时解除锁定，提示按实际值重新保存', async () => {
    const keys: string[] = []
    let saves = 0
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => {
        saves += 1
        keys.push(request.key)
        if (saves === 1) throw new ZbaNetworkError(new Error('offline'))
        return { code: 0, replayed: false, receipt, currentVersion: '2', errors: [] }
      },
      findConfiguration: async () => ({ code: 5 })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    const input = wrapper.get('[data-testid="config-field-botPersona"] textarea')
    await input.setValue('过期键')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="config-validation"]').text()).toContain('操作键已过期')
    expect(input.attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[data-testid="config-save"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    await flushPromises()
    expect(keys).toHaveLength(2)
    expect(keys[0]).not.toBe(keys[1])
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('退出管理员前确认：继续编辑保留草稿，明确放弃后清空', async () => {
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => ({ code: 0, replayed: false, receipt, currentVersion: '2', errors: [] })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()
    const page = wrapper.vm as unknown as {
      requestLeave: (reason: 'lock' | 'downgrade') => Promise<boolean>
    }

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('未保存')
    const pending = page.requestLeave('downgrade')
    await flushPromises()
    const dialog = document.body.querySelector<HTMLElement>('[data-testid="config-leave-dialog"]')
    expect(dialog).not.toBeNull()
    expect(dialog?.textContent).toContain('1 项修改')

    document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')?.click()
    await flushPromises()
    await expect(pending).resolves.toBe(false)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    const second = page.requestLeave('downgrade')
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-discard"]')?.click()
    await flushPromises()
    await expect(second).resolves.toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('无草稿时锁定守卫直接放行，不显示对话框', async () => {
    const wrapper = mountPage({ mode: 'admin' })
    await flushPromises()
    const page = wrapper.vm as unknown as { requestLeave: (reason: 'lock') => Promise<boolean> }

    await expect(page.requestLeave('lock')).resolves.toBe(true)
    expect(wrapper.find('[data-testid="config-leave-dialog"]').exists()).toBe(false)
  })

  it('离开配置视图会清空秘密替换输入，但保留普通字段草稿', async () => {
    const wrapper = mountPage({ mode: 'admin', scope: 'agent', client: clientWith(agentSecretFixture()) })
    await flushPromises()
    const page = wrapper.vm as unknown as { discardSecretsOnLeave: () => void }

    await wrapper.get('[data-testid="config-field-agentPrompt"] textarea').setValue('字段草稿')
    await wrapper.get('[data-testid="config-secrets"] [data-testid="secret-replace"]').trigger('click')
    await wrapper.get('[data-testid="config-secrets"] input[type="password"]').setValue('sk-synthetic-leave-7c41')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')

    page.discardSecretsOnLeave()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="config-secrets"] input[type="password"]')).toHaveLength(0)
    expect((wrapper.get('[data-testid="config-field-agentPrompt"] textarea').element as HTMLTextAreaElement).value).toBe('字段草稿')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
  })

  it('未知结果时离开视图不清秘密输入，留给核对与重存', async () => {
    const client = clientWith(agentSecretFixture(), {
      saveConfiguration: async () => {
        throw new ZbaNetworkError(new Error('offline'))
      }
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: 'agent' })
    await flushPromises()
    const page = wrapper.vm as unknown as { discardSecretsOnLeave: () => void }

    await wrapper.get('[data-testid="config-secrets"] [data-testid="secret-replace"]').trigger('click')
    await wrapper.get('[data-testid="config-secrets"] input[type="password"]').setValue('sk-synthetic-keep-7c41')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)

    page.discardSecretsOnLeave()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="config-secrets"] input[type="password"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
  })

  it('有草稿时刷新会触发浏览器离开确认', async () => {
    const wrapper = mountPage({ mode: 'admin' })
    await flushPromises()

    const clean = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(clean)
    expect(clean.defaultPrevented).toBe(false)

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('脏草稿')
    const dirty = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(dirty)
    expect(dirty.defaultPrevented).toBe(true)
  })

  it('服务端回报 UnknownCommit（12）时保留操作键并冻结编辑', async () => {
    const keys: string[] = []
    const findKeys: string[] = []
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => {
        keys.push(request.key)
        return {
          code: 12,
          replayed: false,
          currentVersion: request.expectedVersion,
          errors: [],
          receipt: { ...receipt, key: request.key, commitState: 2, applyState: 5, committedVersion: undefined }
        }
      },
      findConfiguration: async (key) => {
        findKeys.push(key)
        return { code: 0, receipt }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    const input = wrapper.get('[data-testid="config-field-botPersona"] textarea')
    await input.setValue('未知提交')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="config-check-pending"]').exists()).toBe(true)
    expect(input.attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    expect(findKeys).toEqual([keys[0]])
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('成功码缺少回执时不清草稿，核对回执后才确认已保存', async () => {
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => ({ code: 0, replayed: false, currentVersion: '1', errors: [] }),
      findConfiguration: async () => ({ code: 0, receipt })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    const input = wrapper.get('[data-testid="config-field-botPersona"] textarea')
    await input.setValue('缺少回执')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).not.toContain('已保存')
    expect(input.attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('保存中打开离开对话框：保存与放弃都禁用，且不发第二个请求', async () => {
    let saves = 0
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => {
        saves += 1
        await gate
        return { code: 0, replayed: false, receipt, currentVersion: '2', errors: [] }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()
    const page = wrapper.vm as unknown as {
      requestLeave: (reason: 'lock' | 'downgrade') => Promise<boolean>
    }

    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('在途保存')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    const pending = page.requestLeave('downgrade')
    await flushPromises()
    const saveButton = document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')
    const discardButton = document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-discard"]')
    expect(saveButton?.disabled).toBe(true)
    expect(discardButton?.disabled).toBe(true)
    saveButton?.click()
    discardButton?.click()
    await flushPromises()
    expect(saves).toBe(1)
    await expect(page.requestLeave('lock')).resolves.toBe(true)
    expect(getPendingOperation(client, 'configuration', 'config:basic')?.state).toBe('in-flight')

    release()
    await flushPromises()
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')?.click()
    await flushPromises()
    await expect(pending).resolves.toBe(false)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('配置读取 401 时上报会话失效，不在本页吞掉', async () => {
    let calls = 0
    const client = {
      readConfiguration: async () => {
        calls += 1
        if (calls === 1) return editableFixture
        throw new ZbaRequestError(401, 'unauthorized', 'synthetic expired')
      }
    } as unknown as ZbaClient
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()

    const emitted = wrapper.emitted('auth-expired')
    expect(emitted).toHaveLength(1)
    expect((emitted?.[0]?.[0] as { code: string }).code).toBe('unauthorized')
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(false)
  })

  it('核对响应不具权威性时保留未知态与操作键，不声称没有落盘', async () => {
    const findKeys: string[] = []
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => ({
        code: 12,
        replayed: false,
        currentVersion: request.expectedVersion,
        errors: [],
        receipt: { ...receipt, key: request.key, commitState: 2, applyState: 5, committedVersion: undefined }
      }),
      findConfiguration: async (key) => {
        findKeys.push(key)
        throw new ZbaRequestError(503, 'http_error', 'ZBA 返回 HTTP 503。')
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    const input = wrapper.get('[data-testid="config-field-botPersona"] textarea')
    await input.setValue('核对矛盾')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(input.attributes('disabled')).toBeDefined()
    const validation = wrapper.get('[data-testid="config-validation"]').text()
    expect(validation).toContain('核对未完成')
    expect(validation).not.toContain('没有落盘')

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    expect(findKeys).toHaveLength(2)
    expect(findKeys[0]).toBe(findKeys[1])
  })

  it('保存成功只结束本次提交的改名意图，请求里其余字段一律 Keep', async () => {
    const saveRequests: ZbaConfigurationSaveRequest[] = []
    let current = providerSnapshot('Synthetic Provider A', '1')
    let version = 1
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => {
        saveRequests.push(structuredClone(request) as ZbaConfigurationSaveRequest)
        version += 1
        const written = request.changes.registered?.providers[0]?.changes.nameRaw.value
        current = providerSnapshot(typeof written === 'string' ? written : 'Synthetic Provider A', String(version))
        return {
          code: 0,
          replayed: false,
          receipt: committedReceipt(request, String(version)),
          currentVersion: String(version),
          errors: []
        }
      }
    })
    const scope = registerSyntheticProviderScope()
    const wrapper = mountPage({ client, mode: 'admin', scope })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-fastModel"] input').setValue('改过的人格')
    await renameProvider(wrapper, 'Synthetic Provider A2')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')

    await saveDraft(wrapper)
    await flushPromises()

    expect(saveRequests).toHaveLength(1)
    const request = saveRequests[0]!
    expect(request.changes.model?.fastModel).toEqual({ kind: 1, value: '改过的人格' })
    expect(request.changes.registered?.providers).toHaveLength(1)
    const edit = request.changes.registered!.providers[0]!
    expect(edit.key).toBe('provider-sample')
    expect(edit.changes.nameRaw).toEqual({ kind: 1, value: 'Synthetic Provider A2' })
    for (const key of ['priority', 'baseUrl', 'modelName', 'enabled'] as const) {
      expect(edit.changes[key]).toEqual({ kind: 0 })
    }
    expect(edit.changes.credential).toEqual({ kind: 0 })

    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(true)
    await expectCleanProviderDraft(wrapper, 'Synthetic Provider A2')
  })

  it('已确认落盘后再次改名：新意图照常计入，并只结束这一次提交', async () => {
    const saveRequests: ZbaConfigurationSaveRequest[] = []
    let current = providerSnapshot('Synthetic Provider A', '1')
    let version = 1
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => {
        saveRequests.push(structuredClone(request) as ZbaConfigurationSaveRequest)
        version += 1
        const written = request.changes.registered?.providers[0]?.changes.nameRaw.value
        current = providerSnapshot(typeof written === 'string' ? written : 'Synthetic Provider A', String(version))
        return {
          code: 0,
          replayed: false,
          receipt: committedReceipt(request, String(version)),
          currentVersion: String(version),
          errors: []
        }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: registerSyntheticProviderScope() })
    await flushPromises()

    await renameProvider(wrapper, 'Synthetic Provider A2')
    await saveDraft(wrapper)
    await flushPromises()
    await expectCleanProviderDraft(wrapper, 'Synthetic Provider A2')

    await providerInput(wrapper).setValue('Synthetic Provider A3')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    expect(wrapper.get('[data-testid="config-providers"]').text()).toContain('已改名')

    await saveDraft(wrapper)
    await flushPromises()

    expect(saveRequests).toHaveLength(2)
    expect(saveRequests[1]!.changes.registered?.providers[0]?.changes.nameRaw).toEqual({
      kind: 1,
      value: 'Synthetic Provider A3'
    })
    await expectCleanProviderDraft(wrapper, 'Synthetic Provider A3')
  })

  it('核对确认落盘后结束改名意图，未知期间草稿仍算未保存', async () => {
    const findKeys: string[] = []
    let current = providerSnapshot('Synthetic Provider A', '1')
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => ({
        code: 12,
        replayed: false,
        currentVersion: request.expectedVersion,
        errors: [],
        receipt: { ...receipt, key: request.key, commitState: 2, applyState: 5, committedVersion: undefined }
      }),
      findConfiguration: async (key) => {
        findKeys.push(key)
        current = providerSnapshot('Synthetic Provider A2', '2')
        return { code: 0, receipt: { ...receipt, key, observedVersion: '1', committedVersion: '2' } }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: registerSyntheticProviderScope() })
    await flushPromises()

    await renameProvider(wrapper, 'Synthetic Provider A2')
    await saveDraft(wrapper)

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    expect(wrapper.get('[data-testid="config-providers"]').text()).toContain('已改名')
    expect(providerInput(wrapper).attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(findKeys).toHaveLength(1)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(true)
    await expectCleanProviderDraft(wrapper, 'Synthetic Provider A2')
  })

  it('丢响应与 NotFound 都保留改名草稿，再次保存使用新的操作键', async () => {
    const saveKeys: string[] = []
    const findKeys: string[] = []
    const client = clientWith(providerSnapshot('Synthetic Provider A', '1'), {
      readConfiguration: async () => providerSnapshot('Synthetic Provider A', '1'),
      saveConfiguration: async (request) => {
        saveKeys.push(request.key)
        throw new ZbaNetworkError()
      },
      findConfiguration: async (key) => {
        findKeys.push(key)
        return { code: 14 }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: registerSyntheticProviderScope() })
    await flushPromises()

    await renameProvider(wrapper, 'Synthetic Provider A2')
    await saveDraft(wrapper)

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    expect(wrapper.get('[data-testid="config-providers"]').text()).toContain('已改名')
    expect(providerInput(wrapper).attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()

    expect(findKeys).toEqual([saveKeys[0]])
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="config-validation"]').text()).toContain('没有落盘')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    expect(wrapper.get('[data-testid="config-providers"]').text()).toContain('已改名')

    await saveDraft(wrapper)
    expect(saveKeys).toHaveLength(2)
    expect(saveKeys[1]).not.toBe(saveKeys[0])
  })

  it('冲突不清改名草稿，重读基线后仍按用户意图提交并在这次确认后结束', async () => {
    const saveRequests: ZbaConfigurationSaveRequest[] = []
    let attempts = 0
    let current = providerSnapshot('Synthetic Provider A', '1')
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => {
        saveRequests.push(structuredClone(request) as ZbaConfigurationSaveRequest)
        attempts += 1
        if (attempts === 1) return { code: 3, replayed: false, currentVersion: '9', errors: [] }
        current = providerSnapshot('Synthetic Provider A2', '10')
        return {
          code: 0,
          replayed: false,
          receipt: committedReceipt(request, '10'),
          currentVersion: '10',
          errors: []
        }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: registerSyntheticProviderScope() })
    await flushPromises()

    await renameProvider(wrapper, 'Synthetic Provider A2')
    await saveDraft(wrapper)

    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    expect(wrapper.get('[data-testid="config-providers"]').text()).toContain('已改名')
    expect(providerInput(wrapper).attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="config-reload-conflict"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(false)
    expect(providerInput(wrapper).attributes('disabled')).toBeUndefined()
    expect(providerInput(wrapper).element.value).toBe('Synthetic Provider A2')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await saveDraft(wrapper)
    await flushPromises()

    expect(saveRequests).toHaveLength(2)
    expect(saveRequests[1]!.changes.registered?.providers[0]?.changes.nameRaw).toEqual({
      kind: 1,
      value: 'Synthetic Provider A2'
    })
    await expectCleanProviderDraft(wrapper, 'Synthetic Provider A2')
  })

  it.each(['save', 'apply-failed', 'find'] as const)('SR-NAME-02 同值收敛后 %s 成功结束名称意图，后续普通保存保留外部新名称', async (completion) => {
    const requests: ZbaConfigurationSaveRequest[] = []
    let current = providerSnapshot()
    let confirmed: ZbaConfigurationReceipt = receipt
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => {
        requests.push(structuredClone(request) as ZbaConfigurationSaveRequest)
        const name = request.changes.registered?.providers[0]?.changes.nameRaw.value
        current = providerSnapshot(typeof name === 'string' ? name : current.view!.registered.providers[0]!.name.nameRaw, String(Number(current.version) + 1))
        confirmed = { ...committedReceipt(request, current.version), ...(completion === 'apply-failed' ? { applyState: 4, steps: [{ step: 0, state: 4 }] } : {}) }
        if (completion === 'find' && requests.length === 1) throw new ZbaNetworkError()
        return { code: completion === 'apply-failed' ? 13 : 0, replayed: false, receipt: confirmed, currentVersion: current.version, errors: [] }
      },
      findConfiguration: async () => ({ code: 0, receipt: confirmed })
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: registerSyntheticProviderScope() })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic converged B', '2')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    await wrapper.get('[data-testid="config-nav-model"]').trigger('click')
    await wrapper.get('[data-testid="config-field-fastModel"] input').setValue('Synthetic ordinary edit 1')
    await saveDraft(wrapper)
    expect(requests[0]!.changes.registered?.providers ?? []).toHaveLength(0)
    if (completion === 'find') {
      expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')
      await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
      await flushPromises()
      await flushPromises()
    }
    await wrapper.get('[data-testid="config-nav-registered"]').trigger('click')
    await expectCleanProviderDraft(wrapper, 'Synthetic converged B')
    if (completion === 'apply-failed') expect(wrapper.get('[data-testid="config-receipt"]').text()).toContain('失败')
    current = providerSnapshot('Synthetic later C', '4')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="config-nav-model"]').trigger('click')
    await wrapper.get('[data-testid="config-field-fastModel"] input').setValue('Synthetic ordinary edit 2')
    await saveDraft(wrapper)
    expect(requests).toHaveLength(2)
    expect(requests[1]!.changes.registered?.providers ?? []).toHaveLength(0)
    expect(current.view!.registered.providers[0]!.name.nameRaw).toBe('Synthetic later C')
  })

  it.each(['lock', 'downgrade'] as const)('SR-NAME-02 同值草稿保存并 %s 无需空请求即可完成离开', async (reason) => {
    let current = providerSnapshot()
    let writes = 0
    const wrapper = mountPage({ mode: 'admin', scope: registerSyntheticProviderScope(), client: clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async () => { writes += 1; throw new Error('No empty write expected') }
    }) })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic converged B', '2')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    const page = wrapper.vm as unknown as { requestLeave: (reason: 'lock' | 'downgrade') => Promise<boolean> }
    let allowed: boolean | undefined
    const leaving = page.requestLeave(reason).then((result) => { allowed = result })
    await flushPromises()
    const saveButton = document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')
    if (reason === 'lock') {
      expect(saveButton).toBeNull()
      wrapper.vm.endSession()
    } else {
      expect(saveButton).not.toBeNull()
      saveButton!.click()
    }
    await flushPromises()
    expect(allowed).toBe(true)
    await leaving
    expect(document.body.querySelector('[data-testid="config-leave-save"]')).toBeNull()
    expect(writes).toBe(0)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(false)
    expect(wrapper.vm.draftCount).toBe(0)
  })

  it('SR-NAME-02 纯同值草稿显式保存时就地结束，不发空请求也不伪造回执', async () => {
    let current = providerSnapshot()
    let writes = 0
    const wrapper = mountPage({ mode: 'admin', scope: registerSyntheticProviderScope(), client: clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async () => { writes += 1; throw new Error('No empty write expected') }
    }) })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic converged B', '2')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    await saveDraft(wrapper)
    expect(writes).toBe(0)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(false)
    await expectCleanProviderDraft(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic later C', '3')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    expect(providerInput(wrapper).element.value).toBe('Synthetic later C')
  })

  it.each([3, 4, 12] as const)('SR-NAME-02 保存 code %s 不结束同值名称草稿', async (code) => {
    let current = providerSnapshot()
    const wrapper = mountPage({ mode: 'admin', scope: registerSyntheticProviderScope(), client: clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async () => ({ code, replayed: false, currentVersion: '2', errors: [] })
    }) })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic converged B', '2')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="config-nav-model"]').trigger('click')
    await wrapper.get('[data-testid="config-field-fastModel"] input').setValue('Synthetic ordinary edit')
    await saveDraft(wrapper)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')
  })

  it.each(['save', 'find'] as const)('SR-NAME-02 旧会话迟到的 %s 回执不能清掉新会话同键名称意图', async (completion) => {
    let current = providerSnapshot()
    let release!: () => void
    const delayed = new Promise<void>((resolve) => { release = resolve })
    const requests: ZbaConfigurationSaveRequest[] = []
    const wrapper = mountPage({ mode: 'admin', scope: registerSyntheticProviderScope(), client: clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async (request) => {
        requests.push(request)
        if (requests.length === 1 && completion === 'save') {
          await delayed
          return { code: 0, replayed: false, currentVersion: '2', receipt: committedReceipt(request, '2'), errors: [] }
        }
        throw new ZbaNetworkError()
      },
      findConfiguration: async (key) => { await delayed; return { code: 0, receipt: { ...receipt, key } } }
    }) })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic old B')
    await saveDraft(wrapper)
    if (completion === 'find') await wrapper.get('[data-testid="config-check-pending"]').trigger('click')
    const page = wrapper.vm as unknown as { endSession: () => void; reload: () => Promise<boolean> }
    page.endSession()
    current = providerSnapshot('Synthetic new baseline', '9')
    await page.reload()
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic new C')
    await saveDraft(wrapper)
    expect(requests).toHaveLength(1)
    expect(wrapper.text()).toContain('未决操作')
    release()
    await flushPromises()
    await flushPromises()
    expect(providerInput(wrapper).element.value).toBe('Synthetic new C')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    await saveDraft(wrapper)
    expect(requests).toHaveLength(2)
    expect(requests[1]!.changes.registered!.providers[0]!.changes.nameRaw.value).toBe('Synthetic new C')
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(false)
  })

  it('会话结束丢掉未提交的改名意图，重新读回后不会把旧草稿带给新会话', async () => {
    let current = providerSnapshot('Synthetic Provider A', '1')
    const client = clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async () => {
        throw new Error('会话结束时不得再发写请求')
      }
    })
    const wrapper = mountPage({ client, mode: 'admin', scope: registerSyntheticProviderScope() })
    await flushPromises()

    await renameProvider(wrapper, 'Synthetic Provider A2')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    const page = wrapper.vm as unknown as {
      endSession: () => void
      reload: () => Promise<boolean>
      draftCount: number
    }
    page.endSession()
    await flushPromises()
    // 会话结束后页面不再持有快照，也不该再记着上一会话的名称意图。
    expect(page.draftCount).toBe(0)
    expect(wrapper.find('[data-testid="config-providers"]').exists()).toBe(false)

    // 新会话读回的是基线 A；如果旧的 A2 草稿还留着，这里会立刻变成脏。
    await page.reload()
    await flushPromises()
    await wrapper.get('[data-testid="config-nav-registered"]').trigger('click')
    expect(providerInput(wrapper).element.value).toBe('Synthetic Provider A')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
    expect(wrapper.get('[data-testid="config-providers"]').text()).not.toContain('已改名')
  })
})

  it('平台策略只提交改过的行，行内未改字段保留原始规则 (#104)', async () => {
    const calls: ZbaConfigurationSaveRequest[] = []
    const data = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
    const policy = data.view?.platform.platformPolicies.value?.[0]
    if (!policy) throw new Error('Synthetic editable fixture must include a platform policy')
    for (const field of [policy.enabled, policy.chatEnabled, policy.groupWhitelist, policy.privateWhitelist, policy.allowedActions, policy.features]) field.userMode = 2
    policy.enabled.value = 1; policy.chatEnabled.value = 0
    policy.groupWhitelist.value = { kind: 1, value: '10001' }
    policy.privateWhitelist.value = { kind: 2, value: '' }
    policy.allowedActions.value = { kind: 1, actions: ['like', 'poke'] }
    policy.features.value = [{ feature: 0, enabled: true }]
    const client = clientWith(data, {
      saveConfiguration: async (request) => {
        calls.push(request)
        return { code: 0, replayed: false, receipt, currentVersion: '2', errors: [] }
      }
    })
    // 平台分区只在 platform 能力下渲染（sections: ['platform']）。
    const wrapper = mountPage({ client, mode: 'admin', scope: 'platform' })
    await flushPromises()

    await wrapper.get('[data-testid="config-nav-platform"]').trigger('click')
    const row = wrapper.get('[data-testid="config-policy-instance:sample-platform:sample-account:sample-instance"]')
    // 群名单从「显式 10001」改成「全收」：只有这一行该进请求体。
    await row.get('[data-testid^="policy-group-"][data-testid$="-kind"]').setValue('2')
    // 未修改的动作保持原始规则，不为通过校验而重写其它意图。
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    await flushPromises()

    expect(calls).toHaveLength(1)
    const request = calls[0]!
    const policies = request.changes.registered?.policies ?? []
    expect(policies).toHaveLength(1)
    const edit = policies[0]!
    expect(edit.target).toEqual({
      kind: 'instance',
      key: { platformId: 'sample-platform', accountScope: 'sample-account', instanceId: 'sample-instance' }
    })
    expect(edit.configuration.groupWhitelist).toEqual({ kind: 2 })
    // 行内完整配置保留，明确字段与继承字段均不从 display 物化。
    expect(edit.configuration).toEqual({ enabled: 1, chatEnabled: 0, groupWhitelist: { kind: 2 },
      privateWhitelist: { kind: 2 }, allowedActions: { kind: 1, actions: ['like', 'poke'] },
      features: [{ feature: 0, enabled: true }] })
    // 标量与既有意图不被顺带改写。
    expect(request.changes.behavior).toBeUndefined()
    expect(request.changes.registered?.providers ?? []).toEqual([])
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

describe('ConfigurationPage 生效状态与秘密边界（PNL-05 / PNL-10）', () => {
  async function saveWith(applyState: ZbaApplyState) {
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => ({
        code: 0,
        replayed: false,
        receipt: { ...committedReceipt(request, '2'), applyState, steps: [{ step: 0, state: applyState }] },
        currentVersion: '2',
        errors: []
      })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('新人格')
    await saveDraft(wrapper)
    return wrapper
  }

  it('已保存但需重启：回执显示「需重启」并提示重启后才完全生效，不显示已生效', async () => {
    const wrapper = await saveWith(3)
    const panel = wrapper.get('[data-testid="config-receipt"]').text()
    expect(panel).toContain('已提交')
    expect(panel).toContain('需重启')
    expect(panel).toContain('需要重启机器人服务')
    expect(panel).not.toContain('已保存且热更新')
    expect(panel).not.toContain('已生效')
  })

  it('已保存但应用失败：回执如实显示「失败」，不冒报已生效', async () => {
    const wrapper = await saveWith(4)
    const panel = wrapper.get('[data-testid="config-receipt"]').text()
    expect(panel).toContain('已提交')
    expect(panel).toContain('失败')
    expect(panel).not.toContain('已生效')
  })

  it('部分生效：回执总状态显示「部分生效」，逐步列出失败步骤与错误码，不冒报整体已生效', async () => {
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => ({
        code: 0,
        replayed: false,
        receipt: {
          ...committedReceipt(request, '2'),
          applyState: 2,
          steps: [{ step: 0, state: 1 }, { step: 2, state: 4, errorCode: 'synthetic_projection_failed' }]
        },
        currentVersion: '2',
        errors: []
      })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] textarea').setValue('新人格')
    await saveDraft(wrapper)
    const grid = wrapper.get('[data-testid="config-receipt"] .receipt-grid').text()
    expect(grid).toContain('部分生效')
    expect(grid).not.toContain('已生效')
    const steps = wrapper.get('[data-testid="config-receipt-steps"]').text()
    expect(steps).toContain('发布快照：已生效')
    expect(steps).toContain('模型投影：失败')
    expect(steps).toContain('synthetic_projection_failed')
  })

  it('秘密槽位只给元数据：不回显值，也没有 QQ 官方 Secret 的写入口', async () => {
    const wrapper = mountPage({ mode: 'admin', scope: 'agent', client: clientWith(agentSecretFixture()) })
    await flushPromises()
    const secrets = wrapper.get('[data-testid="config-secrets"]')
    expect(secrets.findAll('li')).toHaveLength(1)
    expect(secrets.text()).not.toMatch(/官方|official/i)
    for (const input of secrets.findAll('input')) {
      expect(input.attributes('type')).toBe('password')
      expect((input.element as HTMLInputElement).value).toBe('')
    }
    expect(wrapper.text()).not.toMatch(/officialAppSecret|官方 App ?Secret/i)
  })

  it('按平台驱动解耦作用域：飞书仅展示飞书字段、飞书 3 槽位秘密与飞书策略，保存不触碰其它平台', async () => {
    const calls: ZbaConfigurationSaveRequest[] = []
    const data = structuredClone(editableFixture) as unknown as ZbaConfigurationReadResult
    const templatePolicy = data.view!.platform.platformPolicies.value![0]!
    const makeField = (id: number, value: unknown) => ({
      visibility: 1 as const,
      display: String(value),
      value,
      userMode: 2 as const,
      effectiveSource: 1 as const,
      applyState: 1 as const,
      rule: { id, effect: 0 as const, canClear: true, editable: true, legacyNeedsReview: false }
    })
    Object.assign(data.view!.platform, {
      feishuEnabled: makeField(56, false),
      feishuAppId: makeField(55, 'cli_synthetic'),
      feishuApiBase: makeField(54, 'https://open.example.com'),
      feishuWhitelist: makeField(57, 'oc_group_1'),
      feishuWhitelistPrivates: makeField(147, ''),
      officialEnabled: makeField(91, false),
      officialAppId: makeField(89, 'official_1'),
      localChannelIds: makeField(67, 'local-1'),
      privateChatEnabled: makeField(104, true),
      whitelistPrivates: makeField(142, ''),
      messageWhitelist: makeField(74, '')
    })
    const feishuPolicy = structuredClone(templatePolicy)
    feishuPolicy.target = { kind: 'instance', key: { platformId: 'feishu', accountScope: 'default', instanceId: 'feishu-main' } }
    const qqPolicy = structuredClone(templatePolicy)
    qqPolicy.target = { kind: 'accountDefault', platformId: 'qq.private', accountScope: 'legacy' }
    data.view!.platform.platformPolicies.value = [qqPolicy, feishuPolicy]

    const client = clientWith(data, {
      saveConfiguration: async (request) => {
        calls.push(request)
        return { code: 0, replayed: false, receipt: committedReceipt(request, '2'), currentVersion: '2', errors: [] }
      }
    })

    // 1. 飞书驱动作用域：仅见飞书 5 字段 + 飞书 3 个秘密 + 飞书策略条目
    const feishu = mountPage({ client, mode: 'admin', scope: 'system.platform-feishu' })
    await flushPromises()
    expect(feishu.find('[data-testid="config-field-feishuEnabled"]').exists()).toBe(true)
    expect(feishu.find('[data-testid="config-field-feishuAppId"]').exists()).toBe(true)
    expect(feishu.find('[data-testid="config-field-whitelistGroups"]').exists()).toBe(false)
    expect(feishu.find('[data-testid="config-field-officialEnabled"]').exists()).toBe(false)
    expect(feishu.find('[data-testid="config-field-localChannelIds"]').exists()).toBe(false)
    expect(feishu.find('[data-testid="config-policy-instance:feishu:default:feishu-main"]').exists()).toBe(true)
    expect(feishu.find('[data-testid="config-policy-account:qq.private:legacy"]').exists()).toBe(false)
    const secretsText = feishu.get('[data-testid="config-secrets"]').text()
    expect(secretsText).toContain('飞书 App Secret')
    expect(secretsText).toContain('飞书 Encrypt Key')
    expect(secretsText).toContain('飞书验证 Token')
    expect(feishu.findAll('[data-testid="config-secrets"] li')).toHaveLength(3)

    // 修改飞书策略并保存，验证只提交飞书策略条目
    const feishuRow = feishu.get('[data-testid="config-policy-instance:feishu:default:feishu-main"]')
    await feishuRow.get('[data-testid="policy-group-instance:feishu:default:feishu-main-kind"]').setValue('2')
    await feishu.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(calls).toHaveLength(1)
    expect(calls[0]!.changes.registered?.policies).toHaveLength(1)
    expect(calls[0]!.changes.registered?.policies[0]!.target).toEqual(feishuPolicy.target)

    // 2. QQ 官方作用域：展示环境变量密钥只读提示且不暴露任何秘密写框
    const official = mountPage({ client, mode: 'admin', scope: 'system.platform-qq-official' })
    await flushPromises()
    expect(official.find('[data-testid="config-field-officialEnabled"]').exists()).toBe(true)
    expect(official.find('[data-testid="config-field-feishuEnabled"]').exists()).toBe(false)
    expect(official.find('[data-testid="config-secrets"]').exists()).toBe(false)
    expect(official.get('[data-testid="config-credential-hint"]').text()).toContain('BOTAGENT_OFFICIAL_APP_SECRET')

    // 3. QQ 私域作用域：仅展示私域字段与私域策略，无秘密框
    const qqPrivate = mountPage({ client, mode: 'admin', scope: 'system.platform-qq-private' })
    await flushPromises()
    expect(qqPrivate.find('[data-testid="config-field-whitelistGroups"]').exists()).toBe(true)
    expect(qqPrivate.find('[data-testid="config-field-feishuAppId"]').exists()).toBe(false)
    expect(qqPrivate.find('[data-testid="config-secrets"]').exists()).toBe(false)
    expect(qqPrivate.find('[data-testid="config-policy-account:qq.private:legacy"]').exists()).toBe(true)
    expect(qqPrivate.find('[data-testid="config-policy-instance:feishu:default:feishu-main"]').exists()).toBe(false)

    // 4. 本地调试作用域：仅展示 localChannelIds
    const local = mountPage({ client, mode: 'admin', scope: 'system.platform-local' })
    await flushPromises()
    expect(local.find('[data-testid="config-field-localChannelIds"]').exists()).toBe(true)
    expect(local.find('[data-testid="config-field-whitelistGroups"]').exists()).toBe(false)
    expect(local.find('[data-testid="config-secrets"]').exists()).toBe(false)
  })
})
