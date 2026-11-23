import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient, type ZbaConversationProfileView } from '../../src/api/zba'
import ConversationProfileEditor from '../../src/features/configuration/ConversationProfileEditor.vue'
import ConfigurationPage from '../../src/features/configuration/ConfigurationPage.vue'
import type { ConversationProfileDrafts } from '../../src/features/configuration/conversation-profile-draft'
import editableFixture from '../fixtures/configuration-read-editable-result.json'

const mounted: ReturnType<typeof mount>[] = []
afterEach(() => mounted.splice(0).forEach((w) => w.unmount()))

const qqDriver = { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' }

function sampleEntry(): ZbaConversationProfileView {
  return {
    key: { driver: qqDriver, conversationKey: 'group:10001' },
    visibility: 1,
    conversationKeyDisplay: 'group:100***01',
    displayLabel: '协作***群',
    displayLabelRaw: '协作工作群',
    style: { personaOverride: null, personaAppend: '简明直接', filterActionNarration: null, voiceMaxChars: null, voiceEagerness: null },
    pacing: { groupInterjectionProbability: 0, proactiveEnabled: 2 },
    capabilities: { features: [{ feature: 6, enabled: false }], toolTier: 2 }
  }
}

describe('ConversationProfileEditor & ConfigurationPage integration', () => {
  it('shows masked display in legend while binding raw label to editable input and supports one-click presets', async () => {
    const drafts: ConversationProfileDrafts = {}
    const wrapper = mount(ConversationProfileEditor, {
      props: {
        platformId: 'qq.private',
        driver: qqDriver,
        entries: [sampleEntry()],
        modelValue: drafts,
        maxMessagesCeiling: 40
      }
    })
    mounted.push(wrapper)

    expect(wrapper.text()).toContain('热更新 · 免重启')
    expect(wrapper.text()).toContain('40 条')
    const existingRow = wrapper.get('[data-testid="conversation-profile-row-group:10001"]')
    expect(existingRow.get('legend').text()).toContain('协作***群')
    expect(existingRow.get('legend').text()).toContain('group:100***01')
    const labelInput = existingRow.get<HTMLInputElement>('[data-testid="conversation-profile-label-group:10001"]')
    expect(labelInput.element.value).toBe('协作工作群')

    // Reject wrong platform prefix
    await wrapper.get('[data-testid="conversation-profile-new-key"]').setValue('feishu:group:oc_1')
    await wrapper.get('[data-testid="conversation-profile-add-blank"]').trigger('click')
    expect(wrapper.get('[data-testid="conversation-profile-add-error"]').text()).toContain('group:10001')

    // Add new profile using restrained-group preset
    await wrapper.get('[data-testid="conversation-profile-new-key"]').setValue('group:10002')
    await wrapper.get('[data-testid="conversation-profile-new-label"]').setValue('研发群')
    await wrapper.get('[data-testid="conversation-profile-add-preset-restrained-group"]').trigger('click')
    const emitted = wrapper.emitted('update:modelValue')
    expect(emitted).toBeDefined()
    const nextDrafts = emitted![0]![0] as ConversationProfileDrafts
    const added = Object.values(nextDrafts).find((e) => e.kind === 'upsert' && e.draft.key.conversationKey === 'group:10002')
    expect(added?.kind).toBe('upsert')
    if (added?.kind === 'upsert') {
      expect(added.draft.groupInterjectionProbability).toBe('0')
      expect(added.draft.proactiveEnabled).toBe(2)
      expect(added.draft.toolTier).toBe(2)
    }
  })

  it('displays cross-platform conversation profile summary in conversation scope and emits open-capabilities(platforms)', async () => {
    const payload = structuredClone(editableFixture) as Record<string, unknown>
    const view = payload.view as Record<string, unknown>
    const registered = view.registered as Record<string, unknown>
    registered.conversationProfiles = [sampleEntry()]

    const fetchImpl = vi.fn(async () =>
      new Response(JSON.stringify(payload), { status: 200, headers: { 'content-type': 'application/json' } })
    ) as unknown as typeof fetch

    const client = new ZbaClient({ token: 'synthetic-admin-token', fetchImpl })
    const wrapper = mount(ConfigurationPage, {
      props: { client, mode: 'admin', writesEnabled: true, scope: 'conversation', embedded: true, active: true }
    })
    mounted.push(wrapper)
    await flushPromises()

    const summary = wrapper.get('[data-testid="conversation-profiles-summary"]')
    expect(summary.text()).toContain('按会话定制档案（1 条）')
    expect(summary.text()).toContain('qq.private（1）')
    await wrapper.get('[data-testid="conversation-profiles-open-platforms"]').trigger('click')
    expect(wrapper.emitted('open-capabilities')).toEqual([['platforms']])
  })
})
