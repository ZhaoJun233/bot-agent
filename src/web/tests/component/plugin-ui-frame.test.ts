import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import PluginUiFrame from '../../src/features/capabilities/PluginUiFrame.vue'
import type { ZbaClient } from '../../src/api/zba'
import type { ZbaPluginUiSession } from '../../src/api/plugin-ui'

const SHA = 'a'.repeat(64)

function session(overrides: Partial<ZbaPluginUiSession> = {}): ZbaPluginUiSession {
  return {
    sessionId: 'uis-frame-1',
    pluginId: 'preset.feature.stickers',
    version: '1.0.0',
    releaseSha256: SHA,
    generationId: 'gen-1',
    contractVersion: 1,
    entryUrl: `/zba/v1/plugins/preset.feature.stickers/ui/${SHA}/ticket-1/web/index.html`,
    allowedActions: ['dialog.close'],
    backendReady: true,
    versionMatched: true,
    adoptionMode: 'online',
    kind: 'dialog',
    title: '表情包协同与安全库',
    expiresAt: '2026-11-20T06:00:00Z',
    ...overrides,
  }
}

describe('PluginUiFrame lifecycle & iframe security', () => {
  it('closes server session immediately when unmounted before openPluginUiSession resolves', async () => {
    let resolveOpen!: (s: ZbaPluginUiSession) => void
    const openPluginUiSession = vi.fn(() => new Promise<ZbaPluginUiSession>(r => { resolveOpen = r }))
    const closePluginUiSession = vi.fn().mockResolvedValue({ sessionId: 'uis-frame-1', pluginId: 'preset.feature.stickers', closed: true })
    const client = { openPluginUiSession, closePluginUiSession } as unknown as ZbaClient

    const wrapper = mount(PluginUiFrame, {
      props: { client, pluginId: 'preset.feature.stickers', mode: 'admin', writesEnabled: true, active: true },
      global: { stubs: { Teleport: true } },
    })

    wrapper.unmount()
    resolveOpen(session())
    await flushPromises()

    expect(closePluginUiSession).toHaveBeenCalledWith('preset.feature.stickers', 'uis-frame-1')
  })

  it('rejects untrusted entryUrl returned by server and closes the lease', async () => {
    const openPluginUiSession = vi.fn().mockResolvedValue(session({ entryUrl: 'https://evil.example/web/index.html' }))
    const closePluginUiSession = vi.fn().mockResolvedValue({ sessionId: 'uis-frame-1', pluginId: 'preset.feature.stickers', closed: true })
    const client = { openPluginUiSession, closePluginUiSession } as unknown as ZbaClient

    const wrapper = mount(PluginUiFrame, {
      props: { client, pluginId: 'preset.feature.stickers', mode: 'admin', writesEnabled: true, active: true },
      global: { stubs: { Teleport: true } },
    })
    await flushPromises()

    expect(wrapper.find('iframe').exists()).toBe(false)
    expect(wrapper.text()).toContain('入口地址无效')
    expect(closePluginUiSession).toHaveBeenCalledWith('preset.feature.stickers', 'uis-frame-1')
    wrapper.unmount()
  })

  it('tears down bridge and closes session if iframe emits a second load event for the same session', async () => {
    const openPluginUiSession = vi.fn().mockResolvedValue(session())
    const closePluginUiSession = vi.fn().mockResolvedValue({ sessionId: 'uis-frame-1', pluginId: 'preset.feature.stickers', closed: true })
    const client = { openPluginUiSession, closePluginUiSession } as unknown as ZbaClient

    const wrapper = mount(PluginUiFrame, {
      props: { client, pluginId: 'preset.feature.stickers', mode: 'admin', writesEnabled: true, active: true },
      global: { stubs: { Teleport: true } },
    })
    await flushPromises()

    const iframe = wrapper.find('iframe')
    expect(iframe.exists()).toBe(true)
    const postMessage = vi.fn()
    Object.defineProperty(iframe.element, 'contentWindow', { value: { postMessage }, configurable: true })

    await iframe.trigger('load')
    expect(postMessage).toHaveBeenCalledTimes(1)

    // Second load event must NOT re-attach bridge; must tear down and report unexpected navigation.
    await iframe.trigger('load')
    await flushPromises()
    expect(postMessage).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('非预期跳转')
    expect(closePluginUiSession).toHaveBeenCalledWith('preset.feature.stickers', 'uis-frame-1')
    wrapper.unmount()
  })
})
