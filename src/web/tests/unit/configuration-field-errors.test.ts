import { describe, expect, it } from 'vitest'
import { configurationFieldErrors, providerErrorPath } from '../../src/features/configuration/field-errors'
import type { ZbaConfigurationReadResult } from '../../src/api/zba'
import fixture from '../fixtures/configuration-read-editable-result.json'

function view() { return (structuredClone(fixture) as unknown as ZbaConfigurationReadResult).view! }
describe('configuration error address resolution', () => {
  it('maps object identity and rule id without marking another provider or same-id scalar', () => {
    const current = view()
    const provider = current.registered.providers[0]!
    provider.name.rule.id = 69
    current.behavior.botPersona!.rule.id = 69
    const other = structuredClone(provider)
    other.key = 'synthetic-other'
    current.registered.providers.push(other)
    const errors = configurationFieldErrors([{ address: { field: 69, object: { kind: 'provider', key: other.key } }, code: 'invalid_name' }], current)
    expect(errors).toEqual([{ path: providerErrorPath(other.key), message: 'synthetic-other / 名称：invalid_name' }])
  })
  it('accepts objectKey addresses and leaves unknown objects unassociated', () => {
    const current = view()
    const provider = current.registered.providers[0]!
    const objectKeyAddress = { field: provider.name.rule.id, objectKey: provider.key }
    const errors = configurationFieldErrors([
      { address: objectKeyAddress, code: 'invalid_name' },
      { address: { field: provider.name.rule.id, object: { kind: 'provider', key: 'synthetic-missing' } }, code: 'invalid_name' }
    ], current)
    expect(errors[0]!.path).toBe(providerErrorPath(provider.key))
    expect(errors[1]!.path).toBeUndefined()
  })
})
