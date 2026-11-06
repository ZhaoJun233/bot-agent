import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ConfigurationPage from '../../src/features/configuration/ConfigurationPage.vue'
import type { ZbaClient, ZbaConfigurationReadResult, ZbaConfigurationSaveRequest } from '../../src/api/zba'
import fixture from '../fixtures/configuration-read-editable-result.json'
import { buildPatch } from '../../src/features/configuration/draft'
import { replyPacingNumberError } from '../../src/features/configuration/reply-pacing'
import { basicFields, fieldOwner } from '../../src/features/capabilities/configuration-ownership'

const defaults = { replyPacingEnabled: false, groupInterjectionProbability: 20,
  groupInterjectionCooldownSeconds: 30, groupContinuationWaitSeconds: 3, privateContinuationWaitSeconds: 1.5 }
function snapshot() {
  const result = structuredClone(fixture) as unknown as ZbaConfigurationReadResult
  result.view!.behavior.aiDesire = { ...result.view!.behavior.botPersona!, visibility: 1,
    value: 50, display: '50', rule: { ...result.view!.behavior.botPersona!.rule, id: 26, editable: true, canClear: true } }
  let id = 148
  for (const [key, value] of Object.entries(defaults)) {
    result.view!.behavior[key] = { ...result.view!.behavior.aiDesire!, visibility: 1,
      value, display: String(value), rule: { ...result.view!.behavior.aiDesire!.rule, id: id++, editable: true } }
  }
  return result
}
const mounted: Array<{ unmount(): void }> = []
afterEach(() => mounted.splice(0).forEach(wrapper => wrapper.unmount()))
let identity = 0
function page(data = snapshot(), save = vi.fn()) {
  const client = { baseUrl: '/synthetic-pacing-' + ++identity, identityLabel: 'synthetic-pacing-' + identity,
    readConfiguration: async () => structuredClone(data), saveConfiguration: save } as unknown as ZbaClient
  const wrapper = mount(ConfigurationPage, { props: { client, mode: 'admin', writesEnabled: true, scope: 'conversation' } })
  mounted.push(wrapper)
  return wrapper
}

describe('reply pacing configuration', () => {
  it('keeps all five controls out of the seven basic fields', () => {
    expect(basicFields.size).toBe(7)
    for (const key of Object.keys(defaults)) {
      expect(basicFields.has(key)).toBe(false)
      expect(fieldOwner('behavior', key)).toBe('conversation')
    }
  })
  it('accepts fractional private wait and rejects nonfinite, fractional integer fields and boundaries', () => {
    for (const n of [0, 0.25, 1.5, 30]) expect(replyPacingNumberError('privateContinuationWaitSeconds', n)).toBeUndefined()
    for (const n of [-1, 30.1, Infinity, -Infinity, NaN]) expect(replyPacingNumberError('privateContinuationWaitSeconds', n)).toBeTruthy()
    for (const [key, max] of [['groupInterjectionProbability', 100], ['groupInterjectionCooldownSeconds', 3600], ['groupContinuationWaitSeconds', 30]] as const) {
      for (const n of [0, max]) expect(replyPacingNumberError(key, n)).toBeUndefined()
      for (const n of [-1, max + 1, 1.5, NaN, Infinity]) expect(replyPacingNumberError(key, n)).toBeTruthy()
    }
    const view = snapshot().view!
    expect(buildPatch({ 'behavior.privateContinuationWaitSeconds': { kind: 'set', raw: '1.5' } }, view))
      .toEqual({ patch: { behavior: { privateContinuationWaitSeconds: { kind: 1, value: 1.5 } } }, errors: [] })
    expect(buildPatch({ 'behavior.groupInterjectionProbability': { kind: 'set', raw: '101' } }, view).errors).toHaveLength(1)
  })
  it('shows truthful defaults, fractional step and disables old desire without clearing its draft', async () => {
    const wrapper = page()
    await flushPromises()
    const desire = wrapper.get<HTMLInputElement>('[data-testid="config-field-aiDesire"] input')
    await desire.setValue('42')
    const desireButtons = wrapper.findAll<HTMLButtonElement>('[data-testid="config-field-aiDesire"] .config-value button')
    const resetDesire = desireButtons.find(button => button.text() === '还原')!
    const clearDesire = desireButtons.find(button => button.text() === '清除')!
    expect(clearDesire.element.disabled).toBe(false)
    const toggle = wrapper.get('[data-testid="config-field-replyPacingEnabled"] select.config-input')
    await toggle.setValue('true')
    expect(desire.element.disabled).toBe(true)
    expect(clearDesire.element.disabled).toBe(true)
    expect(resetDesire.element.disabled).toBe(true)
    expect(desire.element.value).toBe('42')
    expect(wrapper.get('[data-testid="config-field-privateContinuationWaitSeconds"] input').attributes('step')).toBe('any')
    expect(wrapper.get('[data-testid="config-field-privateContinuationWaitSeconds"]').text()).toContain('1.5')
    expect(wrapper.text()).toContain('最后一段')
    expect(wrapper.text()).toContain('当前生效')
    await toggle.setValue('false')
    expect(desire.element.disabled).toBe(false)
    expect(clearDesire.element.disabled).toBe(false)
    expect(desire.element.value).toBe('42')
  })
  it('preserves a fractional draft when server validation fails and maps the exact field', async () => {
    const save = vi.fn(async (_: ZbaConfigurationSaveRequest) => ({ code: 2, replayed: false, receipt: null,
      currentVersion: '1', errors: [{ address: { field: 152, object: null }, code: 'out_of_range' }] }))
    const wrapper = page(snapshot(), save)
    await flushPromises()
    const input = wrapper.get<HTMLInputElement>('[data-testid="config-field-privateContinuationWaitSeconds"] input')
    await input.setValue('2.5')
    await wrapper.get('[data-testid="config-save"]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledOnce()
    expect(save.mock.calls[0]![0].changes.behavior?.privateContinuationWaitSeconds).toEqual({ kind: 1, value: 2.5 })
    expect(input.element.value).toBe('2.5')
    expect(wrapper.get('[data-testid="config-field-errors"]').text()).toContain('out_of_range')
    expect(input.attributes('aria-invalid')).toBe('true')
  })
})
