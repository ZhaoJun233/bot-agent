import { afterEach, describe, expect, it } from 'vitest'
import { ZbaClient, ZbaRequestError } from '../../src/api/zba'
import { createPluginUiBridge } from '../../src/features/capabilities/plugin-ui-bridge'
import { beginOperation, clearPendingOperations, pendingOperations } from '../../src/shared/sync/pending-operations'

afterEach(clearPendingOperations)
function owner() {
  return { client: new ZbaClient({ baseUrl: '/synthetic-ui' }), mode: 'admin' as 'admin' | 'read', writesEnabled: true, active: true }
}
describe('package UI bridge ownership', () => {
  it('does not expose the host client or records from another operation kind', () => {
    const state = owner()
    beginOperation(state.client, { key: 'synthetic-plugin', kind: 'plugin', target: 'synthetic.provider' })
    const bridge = createPluginUiBridge(() => state, 'synthetic.provider', 'quota')
    expect(Object.keys(bridge)).not.toContain('client')
    expect(bridge.operations.list()).toEqual([])
    expect(() => bridge.operations.mark('synthetic-plugin', 'settled')).toThrow('plugin_ui_operation_unavailable')
    expect(pendingOperations(state.client, 'plugin')[0]?.state).toBe('in-flight')
    bridge.operations.begin({ key: 'synthetic-own', target: 'synthetic:quota' })
    const record = bridge.operations.list()[0]!
    expect(Object.keys(record).sort()).toEqual(['key', 'meta', 'state', 'target'])
    expect(Object.isFrozen(record)).toBe(true)
    expect(bridge.operations.list()[0]).toBe(record)
  })
  it('rejects new work after scope changes but settles late results only for its captured owner', () => {
    const state = owner(), original = state.client
    const bridge = createPluginUiBridge(() => state, 'synthetic.provider', 'quota')
    bridge.operations.begin({ key: 'synthetic-original', target: 'synthetic:quota' })
    state.client = new ZbaClient({ baseUrl: '/synthetic-other' })
    const other = createPluginUiBridge(() => state, 'synthetic.provider', 'quota')
    other.operations.begin({ key: 'synthetic-original', target: 'synthetic:quota' })
    expect(() => bridge.operations.begin({ key: 'synthetic-new', target: 'synthetic:new' })).toThrow('plugin_ui_scope_unavailable')
    bridge.operations.mark('synthetic-original', 'settled')
    expect(pendingOperations(original, 'quota')[0]?.state).toBe('settled')
    expect(other.operations.list()[0]?.state).toBe('in-flight')
  })
  it.each(['mode', 'writesEnabled', 'active'] as const)('refuses new work after %s changes', field => {
    const state = owner(), bridge = createPluginUiBridge(() => state, 'synthetic.provider', 'quota')
    if (field === 'mode') state.mode = 'read'
    else state[field] = false
    expect(() => bridge.operations.begin({ key: 'synthetic-new', target: 'synthetic:new' })).toThrow('plugin_ui_scope_unavailable')
  })
  it('classifies only genuine request failures without exposing transport details', () => {
    const state = owner(), bridge = createPluginUiBridge(() => state, 'synthetic.provider', 'quota')
    expect(bridge.requestError({ status: 409, code: 'quota_revision_conflict' })).toBeNull()
    expect(bridge.requestError(new ZbaRequestError(409, 'quota_revision_conflict', 'synthetic-private-detail')))
      .toEqual({ status: 409, code: 'quota_revision_conflict' })
  })
})
