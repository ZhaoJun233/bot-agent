import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ConfigurationPage from '../../src/features/configuration/ConfigurationPage.vue'
import {
  ZbaClient, ZbaNetworkError, ZbaRequestError,
  type ZbaConfigurationReadResult, type ZbaConfigurationReceipt, type ZbaConfigurationSaveRequest
} from '../../src/api/zba'
import editableFixture from '../fixtures/configuration-read-editable-result.json'

const mounted: Array<{ unmount: () => void }> = []

afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
})

function clientWith(
  result: unknown,
  overrides: Partial<Pick<ZbaClient, 'readConfiguration' | 'saveConfiguration' | 'validateConfiguration' | 'findConfiguration'>> = {}
): ZbaClient {
  return {
    readConfiguration: async () => result,
    ...overrides
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

describe('ConfigurationPage', () => {
  it('渲染 8 个分区、字段行与 6 个秘密槽位', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('[data-testid="configuration"]').exists()).toBe(true)
    expect(wrapper.findAll('.config-nav-item')).toHaveLength(8)
    expect(wrapper.findAll('[data-testid="config-fields"] tbody tr')).toHaveLength(3)
    expect(wrapper.get('[data-testid="config-field-botPersona"]').text()).toContain('示例人格')
    expect(wrapper.findAll('[data-testid="config-secrets"] li')).toHaveLength(6)
    expect(wrapper.text()).toContain('版本')
    expect(wrapper.find('[data-testid="config-readonly"]').exists()).toBe(true)
  })

  it('切换分区后显示对应字段，已登记分区显示插件与供应商', async () => {
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.get('[data-testid="config-nav-model"]').trigger('click')
    expect(wrapper.get('[data-testid="config-field-model"]').text()).toContain('sample-model')

    await wrapper.get('[data-testid="config-nav-registered"]').trigger('click')
    expect(wrapper.findAll('[data-testid="config-plugins"] tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('[data-testid="config-providers"] tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('provider-sample')
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

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('新人格')
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

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('冲突值')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)
    expect((wrapper.get('[data-testid="config-field-botPersona"] input').element as HTMLInputElement).value).toBe('冲突值')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
  })

  it('HTTP 5xx 保存结果未知时保留原操作键并允许核对回执', async () => {
    const keys: string[] = []
    const findKeys: string[] = []
    const client = clientWith(editableFixture, {
      saveConfiguration: async (request) => {
        keys.push(request.key)
        throw new ZbaRequestError(503, 'http_error', '合成代理在上游提交后返回 503。')
      },
      findConfiguration: async (key) => {
        findKeys.push(key)
        return { code: 0, receipt: { ...receipt, key } }
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('合成 503 待核对')
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

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('待核对')
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

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('未知结果')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    const input = wrapper.get('[data-testid="config-field-botPersona"] input')
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

    const input = wrapper.get('[data-testid="config-field-botPersona"] input')
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

  it('锁定前确认：继续编辑保留草稿，明确放弃后清空', async () => {
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => ({ code: 0, replayed: false, receipt, currentVersion: '2', errors: [] })
    })
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()
    const page = wrapper.vm as unknown as {
      requestLeave: (reason: 'lock' | 'downgrade') => Promise<boolean>
    }

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('未保存')
    const pending = page.requestLeave('lock')
    await flushPromises()
    const dialog = document.body.querySelector<HTMLElement>('[data-testid="config-leave-dialog"]')
    expect(dialog).not.toBeNull()
    expect(dialog?.textContent).toContain('1 项修改')

    document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-stay"]')?.click()
    await flushPromises()
    await expect(pending).resolves.toBe(false)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')

    const second = page.requestLeave('lock')
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
    const wrapper = mountPage({ mode: 'admin' })
    await flushPromises()
    const page = wrapper.vm as unknown as { discardSecretsOnLeave: () => void }

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('字段草稿')
    await wrapper.get('[data-testid="config-secrets"] [data-testid="secret-replace"]').trigger('click')
    await wrapper.get('[data-testid="config-secrets"] input[type="password"]').setValue('sk-synthetic-leave-7c41')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')

    page.discardSecretsOnLeave()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="config-secrets"] input[type="password"]')).toHaveLength(0)
    expect((wrapper.get('[data-testid="config-field-botPersona"] input').element as HTMLInputElement).value).toBe('字段草稿')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
  })

  it('未知结果时离开视图不清秘密输入，留给核对与重存', async () => {
    const client = clientWith(editableFixture, {
      saveConfiguration: async () => {
        throw new ZbaNetworkError(new Error('offline'))
      }
    })
    const wrapper = mountPage({ client, mode: 'admin' })
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

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('脏草稿')
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

    const input = wrapper.get('[data-testid="config-field-botPersona"] input')
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

    const input = wrapper.get('[data-testid="config-field-botPersona"] input')
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

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('在途保存')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()

    const pending = page.requestLeave('lock')
    await flushPromises()
    const saveButton = document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-save"]')
    const discardButton = document.body.querySelector<HTMLButtonElement>('[data-testid="config-leave-discard"]')
    expect(saveButton?.disabled).toBe(true)
    expect(discardButton?.disabled).toBe(true)
    saveButton?.click()
    discardButton?.click()
    await flushPromises()
    expect(saves).toBe(1)

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

    const input = wrapper.get('[data-testid="config-field-botPersona"] input')
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
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()

    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('改过的人格')
    await renameProvider(wrapper, 'Synthetic Provider A2')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')

    await saveDraft(wrapper)
    await flushPromises()

    expect(saveRequests).toHaveLength(1)
    const request = saveRequests[0]!
    expect(request.changes.behavior?.botPersona).toEqual({ kind: 1, value: '改过的人格' })
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
    const wrapper = mountPage({ client, mode: 'admin' })
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
    const wrapper = mountPage({ client, mode: 'admin' })
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
    const wrapper = mountPage({ client, mode: 'admin' })
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
    const wrapper = mountPage({ client, mode: 'admin' })
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
    const wrapper = mountPage({ client, mode: 'admin' })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic converged B', '2')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
    await wrapper.get('[data-testid="config-nav-behavior"]').trigger('click')
    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic ordinary edit 1')
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
    await wrapper.get('[data-testid="config-nav-behavior"]').trigger('click')
    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic ordinary edit 2')
    await saveDraft(wrapper)
    expect(requests).toHaveLength(2)
    expect(requests[1]!.changes.registered?.providers ?? []).toHaveLength(0)
    expect(current.view!.registered.providers[0]!.name.nameRaw).toBe('Synthetic later C')
  })

  it.each(['lock', 'downgrade'] as const)('SR-NAME-02 同值草稿保存并 %s 无需空请求即可完成离开', async (reason) => {
    let current = providerSnapshot()
    let writes = 0
    const wrapper = mountPage({ mode: 'admin', client: clientWith(current, {
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
    expect(saveButton).not.toBeNull()
    saveButton!.click()
    await flushPromises()
    expect(allowed).toBe(true)
    await leaving
    expect(document.body.querySelector('[data-testid="config-leave-save"]')).toBeNull()
    expect(writes).toBe(0)
    expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('0 项修改')
  })

  it('SR-NAME-02 纯同值草稿显式保存时就地结束，不发空请求也不伪造回执', async () => {
    let current = providerSnapshot()
    let writes = 0
    const wrapper = mountPage({ mode: 'admin', client: clientWith(current, {
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
    const wrapper = mountPage({ mode: 'admin', client: clientWith(current, {
      readConfiguration: async () => current,
      saveConfiguration: async () => ({ code, replayed: false, currentVersion: '2', errors: [] })
    }) })
    await flushPromises()
    await renameProvider(wrapper, 'Synthetic converged B')
    current = providerSnapshot('Synthetic converged B', '2')
    await wrapper.get('[data-testid="config-reload"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="config-nav-behavior"]').trigger('click')
    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic ordinary edit')
    await saveDraft(wrapper)
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('2 项修改')
  })

  it.each(['save', 'find'] as const)('SR-NAME-02 旧会话迟到的 %s 回执不能清掉新会话同键名称意图', async (completion) => {
    let current = providerSnapshot()
    let release!: () => void
    const delayed = new Promise<void>((resolve) => { release = resolve })
    const requests: ZbaConfigurationSaveRequest[] = []
    const wrapper = mountPage({ mode: 'admin', client: clientWith(current, {
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
    expect(requests).toHaveLength(2)
    release()
    await flushPromises()
    await flushPromises()
    expect(providerInput(wrapper).element.value).toBe('Synthetic new C')
    expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
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
    const wrapper = mountPage({ client, mode: 'admin' })
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
