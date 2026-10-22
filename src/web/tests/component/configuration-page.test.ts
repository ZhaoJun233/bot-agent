import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ConfigurationPage from '../../src/features/configuration/ConfigurationPage.vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaConfigurationReceipt } from '../../src/api/zba'
import editableFixture from '../fixtures/configuration-read-editable-result.json'

const mounted: Array<{ unmount: () => void }> = []

afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
})

function clientWith(
  result: unknown,
  overrides: Partial<Pick<ZbaClient, 'saveConfiguration' | 'validateConfiguration' | 'findConfiguration'>> = {}
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
})
