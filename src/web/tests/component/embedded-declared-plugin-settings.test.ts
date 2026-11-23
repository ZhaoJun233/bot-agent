import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import EmbeddedDeclaredPluginSettings from '../../src/features/capabilities/EmbeddedDeclaredPluginSettings.vue'
import { pluginSettings, settingsFor } from '../../src/features/capabilities/plugin-settings'
import type { ZbaClient, ZbaPluginSettings } from '../../src/api/zba'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
})

describe('EmbeddedDeclaredPluginSettings & Phase 2 package catalog', () => {
  it('registers all bundled package presentations including documents, digest and knowledge', () => {
    expect(pluginSettings.length).toBe(24)
    expect(settingsFor('preset.feature.documents')?.title).toContain('文档')
    expect(settingsFor('preset.feature.digest')?.title).toContain('纪要')
    expect(settingsFor('preset.feature.knowledge')?.title).toContain('知识库')
    expect(settingsFor('preset.feature.scheduler')?.title).toContain('定时与提醒')
    expect(settingsFor('preset.feature.personal-todo')?.title).toContain('个人待办')
    expect(settingsFor('system.platform-feishu')?.summary).toContain('长连接')
    expect(settingsFor('system.mcp-tools')?.title).toContain('MCP')
  })

  it('loads and saves hybrid declarative settings and wipes secret drafts without echoing', async () => {
    const initial: ZbaPluginSettings = {
      revision: '0',
      restartRequired: false,
      values: {
        sttEnabled: 'false',
        sttModel: 'whisper-1'
      },
      configuredSecrets: { sttApiKey: false },
      schema: [
        { key: 'sttEnabled', label: '启用云端语音转写（STT）', type: 'boolean', defaultValue: 'false', required: false },
        { key: 'sttModel', label: 'STT 模型名称', type: 'string', defaultValue: 'whisper-1', required: false },
        { key: 'sttApiKey', label: 'STT 专用 API Key', type: 'secret', defaultValue: null, required: false }
      ]
    }

    const saveSpy = vi.fn(async (): Promise<ZbaPluginSettings> => ({
      ...initial,
      revision: '1',
      values: { sttEnabled: 'true', sttModel: 'whisper-large-v3' },
      configuredSecrets: { sttApiKey: true }
    }))

    const fakeClient = {
      pluginSettings: vi.fn(async () => initial),
      savePluginSettings: saveSpy
    } as unknown as ZbaClient

    const wrapper = mount(EmbeddedDeclaredPluginSettings, {
      props: {
        client: fakeClient,
        mode: 'admin',
        writesEnabled: true,
        active: true,
        pluginId: 'preset.feature.voice'
      }
    })
    mounted.push(wrapper)
    await flushPromises()

    expect(wrapper.find('[data-testid="embedded-declared-plugin-settings"]').exists()).toBe(true)
    await wrapper.get('[data-testid="plugin-setting-sttEnabled"]').setValue(true)
    await wrapper.get('[data-testid="plugin-setting-sttModel"]').setValue('whisper-large-v3')
    await wrapper.get('[data-testid="plugin-setting-sttApiKey-mode"]').setValue('set')
    await flushPromises()
    await wrapper.get('[data-testid="plugin-setting-sttApiKey"]').setValue('sk-synthetic-stt-secret-never-echo')

    await wrapper.get('[data-testid="embedded-plugin-settings-save"]').trigger('click')
    await flushPromises()

    expect(saveSpy).toHaveBeenCalledTimes(1)
    expect(wrapper.html()).not.toContain('sk-synthetic-stt-secret-never-echo')
    expect(wrapper.get('.embedded-notice').attributes('data-tone')).toBe('ok')
    expect(wrapper.emitted('saved')?.length).toBe(1)
  })

  it('guards dirty close with inline discard confirmation and preserves secrets until confirmed', async () => {
    const initial: ZbaPluginSettings = {
      revision: '3',
      restartRequired: false,
      values: {
        transportMode: 'webhook',
        enableInteractiveCards: 'true'
      },
      configuredSecrets: { appSecret: false },
      schema: [
        { key: 'transportMode', label: '模式', type: 'string', defaultValue: 'webhook', required: false },
        { key: 'enableInteractiveCards', label: '卡片', type: 'boolean', defaultValue: 'true', required: false },
        { key: 'appSecret', label: '密钥', type: 'secret', defaultValue: null, required: false }
      ]
    }
    const fakeClient = {
      pluginSettings: vi.fn(async () => initial),
      savePluginSettings: vi.fn(async (): Promise<ZbaPluginSettings> => ({
        ...initial,
        revision: '4',
        restartRequired: true,
        values: { transportMode: 'long_connection', enableInteractiveCards: 'true' }
      }))
    } as unknown as ZbaClient

    const wrapper = mount(EmbeddedDeclaredPluginSettings, {
      props: {
        client: fakeClient,
        mode: 'admin',
        writesEnabled: true,
        active: true,
        pluginId: 'system.platform-feishu'
      }
    })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('[data-testid="plugin-setting-appSecret-mode"]').setValue('set')
    await flushPromises()
    await wrapper.get('[data-testid="plugin-setting-appSecret"]').setValue('synthetic-secret-keep-on-cancel')

    const vm = wrapper.vm as unknown as {
      requestLeave: (reason: 'close' | 'lock' | 'downgrade' | 'navigate') => Promise<boolean>
      dirty: boolean
    }
    const leaveCanceled = vm.requestLeave('close')
    await flushPromises()
    expect(wrapper.find('[data-testid="embedded-plugin-discard-confirmation"]').exists()).toBe(true)
    await wrapper.get('[data-testid="embedded-plugin-discard-cancel"]').trigger('click')
    await flushPromises()
    expect(await leaveCanceled).toBe(false)
    expect((wrapper.get('[data-testid="plugin-setting-appSecret"]').element as HTMLInputElement).value).toBe('synthetic-secret-keep-on-cancel')

    const leaveConfirmed = vm.requestLeave('close')
    await flushPromises()
    await wrapper.get('[data-testid="embedded-plugin-discard-confirm"]').trigger('click')
    await flushPromises()
    expect(await leaveConfirmed).toBe(true)
    expect(wrapper.html()).not.toContain('synthetic-secret-keep-on-cancel')

    await wrapper.get('[data-testid="plugin-setting-transportMode"]').setValue('long_connection')
    await wrapper.get('[data-testid="embedded-plugin-settings-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.embedded-notice').attributes('data-tone')).toBe('warn')
  })
})
