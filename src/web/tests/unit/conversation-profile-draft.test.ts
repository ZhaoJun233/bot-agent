import { describe, expect, it } from 'vitest'
import {
  ZbaClient,
  type ZbaConfigurationPatch,
  type ZbaConfigurationView,
  type ZbaConversationProfileView
} from '../../src/api/zba'
import {
  CONVERSATION_PROFILE_FEATURES,
  CONVERSATION_PROFILE_PRESETS,
  CONVERSATION_TOOL_TIERS,
  applyConversationProfileEdits,
  conversationProfileBaseline,
  conversationProfileDirty,
  conversationProfileId,
  defaultDriverForPlatform,
  emptyConversationProfileDraft,
  filterProfilesByPlatform,
  validateConversationKeyPrefix,
  type ConversationProfileDrafts
} from '../../src/features/configuration/conversation-profile-draft'
import { configurationFieldErrors } from '../../src/features/configuration/field-errors'
import editableFixture from '../fixtures/configuration-read-editable-result.json'

function sampleProfileView(
  platformId = 'qq.private',
  conversationKey = 'group:10001'
): ZbaConversationProfileView {
  return {
    key: {
      driver: { platformId, accountScope: platformId === 'feishu' ? 'default' : 'legacy', instanceId: '' },
      conversationKey
    },
    visibility: 1,
    conversationKeyDisplay: platformId === 'qq.private' ? 'group:100***01' : `${platformId}:masked`,
    displayLabel: '项目***群',
    displayLabelRaw: '项目协作群',
    style: {
      personaOverride: null,
      personaAppend: '结论先行',
      filterActionNarration: true,
      voiceMaxChars: 80,
      voiceEagerness: 15
    },
    pacing: {
      groupInterjectionProbability: 0,
      groupInterjectionCooldownSeconds: 60,
      groupContinuationWaitSeconds: 2,
      privateContinuationWaitSeconds: null,
      suitabilityThreshold: 55,
      maxContextMessages: 12,
      proactiveEnabled: 2,
      proactiveQuietSeconds: 300
    },
    capabilities: {
      features: [
        { feature: 6, enabled: false },
        { feature: 10, enabled: true }
      ],
      toolTier: 2
    }
  }
}

function viewWithProfiles(profiles: ZbaConversationProfileView[]): ZbaConfigurationView {
  const cloned = structuredClone(editableFixture.view) as unknown as ZbaConfigurationView
  cloned.registered.conversationProfiles = profiles
  return cloned
}

describe('conversation profile drafts & wire contracts', () => {
  it('enforces platform-specific conversationKey prefixes strictly', () => {
    expect(validateConversationKeyPrefix('qq.private', 'group:10001')).toBeNull()
    expect(validateConversationKeyPrefix('qq.private', 'private:20001')).toBeNull()
    expect(validateConversationKeyPrefix('qq.private', 'feishu:group:oc_1')).not.toBeNull()
    expect(validateConversationKeyPrefix('qq.private', 'group:abc')).not.toBeNull()

    expect(validateConversationKeyPrefix('qq.official', 'official:group:80001')).toBeNull()
    expect(validateConversationKeyPrefix('qq.official', 'group:10001')).not.toBeNull()

    expect(validateConversationKeyPrefix('feishu', 'feishu:group:oc_10001')).toBeNull()
    expect(validateConversationKeyPrefix('feishu', 'group:10001')).not.toBeNull()

    expect(validateConversationKeyPrefix('local', 'local:room-1')).toBeNull()
    expect(validateConversationKeyPrefix('local', 'room-1')).not.toBeNull()
  })

  it('exposes implemented Phase 1-5 features and tool tiers without unimplemented placeholders', () => {
    expect(CONVERSATION_PROFILE_FEATURES.map((f) => f.id)).toEqual([2, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17])
    expect(CONVERSATION_TOOL_TIERS.map((t) => t.value)).toEqual([0, 1, 2, 3])
    expect(CONVERSATION_PROFILE_PRESETS.map((p) => p.id)).toEqual([
      'active-group',
      'restrained-group',
      'personal-assistant'
    ])
  })

  it('produces minimal delta edits for upsert and row-level delete without clobbering sibling registered arrays', () => {
    const existingA = sampleProfileView('qq.private', 'group:10001')
    const existingB = sampleProfileView('feishu', 'feishu:group:oc_10002')
    const view = viewWithProfiles([existingA, existingB])

    expect(filterProfilesByPlatform([existingA, existingB], 'qq.private')).toHaveLength(1)
    expect(conversationProfileDirty(conversationProfileBaseline(existingA), conversationProfileBaseline(existingA))).toBe(false)

    const idA = conversationProfileId(existingA.key)
    const idB = conversationProfileId(existingB.key)
    const keyC = { driver: { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' }, conversationKey: 'group:10003' }
    const presetDraft = CONVERSATION_PROFILE_PRESETS[1]!.apply(emptyConversationProfileDraft(keyC, '克制群'))

    const drafts: ConversationProfileDrafts = {
      [idA]: { kind: 'upsert', draft: conversationProfileBaseline(existingA) }, // unchanged -> omitted
      [idB]: { kind: 'delete', key: existingB.key },
      [conversationProfileId(keyC)]: { kind: 'upsert', draft: presetDraft }
    }

    const patch: ZbaConfigurationPatch = {
      registered: { plugins: [{ key: 'web-search', enabled: true }], providers: [], policies: [] }
    }
    const errors = applyConversationProfileEdits(patch, drafts, view)
    expect(errors).toEqual([])
    expect(patch.registered?.plugins).toEqual([{ key: 'web-search', enabled: true }])
    expect(patch.registered?.conversationProfiles).toHaveLength(2)

    const delEdit = patch.registered?.conversationProfiles?.find((e) => e.operation === 1)
    expect(delEdit?.key).toEqual(existingB.key)
    expect(delEdit?.profile).toBeNull()

    const upsertEdit = patch.registered?.conversationProfiles?.find((e) => e.operation === 0)
    expect(upsertEdit?.key).toEqual(keyC)
    expect(upsertEdit?.profile?.pacing?.groupInterjectionProbability).toBe(0)
    expect(upsertEdit?.profile?.pacing?.proactiveEnabled).toBe(2)
    expect(upsertEdit?.profile?.capabilities?.toolTier).toBe(2)
  })

  it('validates persona caps and numeric bounds locally before submit', () => {
    const key = { driver: { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' }, conversationKey: 'group:10001' }
    const bad = emptyConversationProfileDraft(key)
    bad.personaOverride = '甲'.repeat(900)
    bad.personaAppend = '乙'.repeat(400) // combined 1300 > 1200
    bad.groupInterjectionProbability = '150'
    bad.voiceMaxChars = '5'
    bad.maxContextMessages = '3'

    const patch: ZbaConfigurationPatch = {}
    const errors = applyConversationProfileEdits(patch, { [conversationProfileId(key)]: { kind: 'upsert', draft: bad } }, undefined)
    expect(errors.length).toBeGreaterThanOrEqual(4)
  })

  it('decodes conversationProfiles and conversationProfile runtimeEffects from readConfiguration', async () => {
    const payload = structuredClone(editableFixture) as Record<string, unknown>
    const view = payload.view as Record<string, unknown>
    const registered = view.registered as Record<string, unknown>
    const profile = sampleProfileView('qq.private', 'group:10001')
    registered.conversationProfiles = [profile]
    payload.runtimeEffects = [
      {
        field: { field: 153, object: { kind: 'conversationProfile', key: profile.key } },
        state: 'current',
        reason: 'liveMatch',
        restartTarget: 'none'
      }
    ]

    const fetchImpl = (async () =>
      new Response(JSON.stringify(payload), { status: 200, headers: { 'content-type': 'application/json' } })
    ) as unknown as typeof fetch

    const result = await new ZbaClient({ token: 'synthetic-token', fetchImpl }).readConfiguration(1)
    expect(result.view?.registered.conversationProfiles).toHaveLength(1)
    expect(result.view?.registered.conversationProfiles?.[0]?.displayLabelRaw).toBe('项目协作群')
    expect(result.runtimeEffects?.[0]?.field.object).toEqual({ kind: 'conversationProfile', key: profile.key })
  })

  it('maps server field 153 errors with conversationProfile object address to readable messages', () => {
    const errors = configurationFieldErrors([
      {
        address: {
          field: 153,
          object: {
            kind: 'conversationProfile',
            key: {
              driver: { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' },
              conversationKey: 'group:10001'
            }
          }
        },
        code: 'invalid_conversation_profile'
      }
    ])
    expect(errors).toEqual([
      {
        path: 'registered.conversationProfiles',
        message: '会话定制档案（qq.private / group:10001）：invalid_conversation_profile'
      }
    ])
  })

  it('resolves Feishu default driver to declared instanceId feishu-main and isolates filterProfilesByPlatform by exact driverKey', () => {
    const feishuAccountRow = {
      target: { kind: 'accountDefault' as const, platformId: 'feishu', accountScope: 'default' }
    } as never
    expect(defaultDriverForPlatform('feishu', [feishuAccountRow])).toEqual({
      platformId: 'feishu',
      accountScope: 'default',
      instanceId: 'feishu-main'
    })
    expect(defaultDriverForPlatform('feishu', [])).toEqual({
      platformId: 'feishu',
      accountScope: 'default',
      instanceId: 'feishu-main'
    })

    const feishuMain = sampleProfileView('feishu', 'feishu:group:oc_10001')
    feishuMain.key.driver.instanceId = 'feishu-main'
    const feishuAlt = sampleProfileView('feishu', 'feishu:group:oc_10002')
    feishuAlt.key.driver.instanceId = 'feishu-alt'
    expect(
      filterProfilesByPlatform([feishuMain, feishuAlt], 'feishu', {
        platformId: 'feishu',
        accountScope: 'default',
        instanceId: 'feishu-main'
      })
    ).toEqual([feishuMain])
  })
})
