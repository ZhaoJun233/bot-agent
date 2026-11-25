import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import McpServersCard from '../../src/features/configuration/McpServersCard.vue'
import AccessRolesCard from '../../src/features/configuration/AccessRolesCard.vue'
import type {
  ZbaAccessRolesResponse,
  ZbaAccessRolesSaveRequest,
  ZbaClient,
  ZbaMcpServersResponse,
  ZbaMcpServerSaveRequest
} from '../../src/api/zba'
import { workspaceRoleLabel } from '../../src/shared/friendly'

const mounted: Array<{ unmount: () => void }> = []

afterEach(() => {
  for (const w of mounted.splice(0)) w.unmount()
})

function makeMcpClient(initial: ZbaMcpServersResponse): {
  client: ZbaClient
  saveSpy: ReturnType<typeof vi.fn>
  deleteSpy: ReturnType<typeof vi.fn>
  testSpy: ReturnType<typeof vi.fn>
} {
  let state = structuredClone(initial)
  const testSpy = vi.fn(async () => ({
    ok: true,
    reasonCode: 'ok',
    tools: [
      { name: 'search_docs', description: 'Search docs', readOnlyHint: true },
      { name: 'create_ticket', description: 'Create ticket', readOnlyHint: false }
    ]
  }))
  const saveSpy = vi.fn(async (req: ZbaMcpServerSaveRequest) => {
    state = {
      ...state,
      revision: state.revision + 1,
      servers: [
        ...state.servers.filter((s) => s.id !== req.id),
        {
          id: req.id,
          enabled: req.enabled,
          source: 'console',
          readOnlyLocked: false,
          endpoint: req.endpoint,
          allowedTools: req.allowedTools,
          readOnlyTools: req.readOnlyTools ?? [],
          callTimeoutSeconds: req.callTimeoutSeconds ?? 15,
          maxResultBytes: req.maxResultBytes ?? 16384,
          hasAuthToken: !req.clearAuthToken && (!!req.authToken || state.servers.some((s) => s.id === req.id && s.hasAuthToken))
        }
      ]
    }
    return { ok: true, replayed: false, reasonCode: null, snapshot: state }
  })
  const deleteSpy = vi.fn(async (req: { expectedRevision: number; idempotencyKey: string; id: string }) => {
    state = {
      ...state,
      revision: state.revision + 1,
      servers: state.servers.filter((s) => s.id !== req.id)
    }
    return { ok: true, replayed: false, reasonCode: null, snapshot: state }
  })

  const client = {
    listMcpServers: async () => structuredClone(state),
    testMcpServer: testSpy,
    saveMcpServer: saveSpy,
    deleteMcpServer: deleteSpy,
    reloadMcpServers: async () => ({ ok: true, toolCount: state.discoveredTools.length, snapshot: state })
  } as unknown as ZbaClient

  return { client, saveSpy, deleteSpy, testSpy }
}

function makeRbacClient(initial: ZbaAccessRolesResponse): {
  client: ZbaClient
  saveSpy: ReturnType<typeof vi.fn>
} {
  let state = structuredClone(initial)
  const saveSpy = vi.fn(async (req: ZbaAccessRolesSaveRequest) => {
    state = {
      revision: state.revision + 1,
      masked: true,
      items: req.bindings.map((b) => ({
        platformId: b.platformId,
        accountScope: b.accountScope,
        instanceId: b.instanceId,
        scopeConversationKey: b.scopeConversationKey ? 'group:***01' : null,
        scopeConversationKeyRaw: b.scopeConversationKey,
        nativeUserId: b.nativeUserId.length > 4 ? `${b.nativeUserId.slice(0, 3)}***${b.nativeUserId.slice(-2)}` : '***',
        nativeUserIdRaw: b.nativeUserId,
        role: b.role,
        note: b.note ?? null
      }))
    }
    return { ok: true, replayed: false, reasonCode: null, snapshot: state }
  })

  const client = {
    listAccessRoles: async () => structuredClone(state),
    saveAccessRoles: saveSpy
  } as unknown as ZbaClient

  return { client, saveSpy }
}

describe('Phase 4 McpServersCard & AccessRolesCard', () => {
  it('locks env MCP servers, probes candidates, preserves write-only token on blank edit, and wipes token draft on leave', async () => {
    const { client, saveSpy, testSpy } = makeMcpClient({
      revision: 1,
      allowPrivate: false,
      servers: [
        {
          id: 'env-docs',
          enabled: true,
          source: 'env',
          readOnlyLocked: true,
          endpoint: 'https://env.example.com/mcp',
          allowedTools: ['lookup'],
          readOnlyTools: ['lookup'],
          callTimeoutSeconds: 15,
          maxResultBytes: 16384,
          hasAuthToken: true
        },
        {
          id: 'console-ops',
          enabled: true,
          source: 'console',
          readOnlyLocked: false,
          endpoint: 'https://mcp.example.com/mcp',
          allowedTools: ['search_docs'],
          readOnlyTools: ['search_docs'],
          callTimeoutSeconds: 10,
          maxResultBytes: 8192,
          hasAuthToken: true
        }
      ],
      discoveredTools: [
        {
          qualifiedName: 'mcp.console_ops.search_docs',
          serverId: 'console-ops',
          name: 'search_docs',
          description: 'Search docs',
          readOnlyHint: true,
          readOnlyConfirmed: true
        }
      ]
    })

    const wrapper = mount(McpServersCard, {
      props: { client, mode: 'admin', writesEnabled: true, active: true }
    })
    mounted.push(wrapper)
    await flushPromises()

    expect(wrapper.get('[data-testid="mcp-server-row-env-docs"]').text()).toContain('环境变量只读')
    expect(wrapper.find('[data-testid="mcp-edit-env-docs"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="mcp-delete-env-docs"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="mcp-edit-console-ops"]').exists()).toBe(true)

    await wrapper.get('[data-testid="mcp-edit-console-ops"]').trigger('click')
    const tokenInput = wrapper.get('[data-testid="mcp-input-token"]').element as HTMLInputElement
    expect(tokenInput.value).toBe('')

    // Secret clearing lifecycle
    await wrapper.get('[data-testid="mcp-input-token"]').setValue('sk-draft-secret')
    expect((wrapper.get('[data-testid="mcp-input-token"]').element as HTMLInputElement).value).toBe('sk-draft-secret')
    wrapper.vm.discardSecretsOnLeave()
    await flushPromises()
    expect((wrapper.get('[data-testid="mcp-input-token"]').element as HTMLInputElement).value).toBe('')

    // Probe populates tool toggles and blank authToken omits token in save payload
    await wrapper.get('[data-testid="mcp-test-btn"]').trigger('click')
    await flushPromises()
    expect(testSpy).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="mcp-probe-toggle-create_ticket"]').exists()).toBe(true)
    await wrapper.get('[data-testid="mcp-probe-toggle-create_ticket"]').trigger('click')
    await wrapper.get('[data-testid="mcp-editor-form"]').trigger('submit.prevent')
    await flushPromises()

    expect(saveSpy).toHaveBeenCalledTimes(1)
    const sent = saveSpy.mock.calls[0]?.[0] as ZbaMcpServerSaveRequest
    expect(sent.authToken).toBeNull()
    expect(sent.clearAuthToken).toBe(false)
    expect(sent.allowedTools).toEqual(['search_docs', 'create_ticket'])
    expect(sent.readOnlyTools).toEqual(['search_docs'])

    // Mutual exclusion between authToken input and clearAuthToken checkbox
    await wrapper.get('[data-testid="mcp-edit-console-ops"]').trigger('click')
    await wrapper.get('[data-testid="mcp-input-token"]').setValue('sk-draft-secret-2')
    await wrapper.get('[data-testid="mcp-input-clear-token"]').setValue(true)
    await flushPromises()
    expect((wrapper.get('[data-testid="mcp-input-token"]').element as HTMLInputElement).disabled).toBe(true)
    expect((wrapper.get('[data-testid="mcp-input-token"]').element as HTMLInputElement).value).toBe('')

    wrapper.vm.endSession()
    await flushPromises()
    expect(wrapper.find('[data-testid="mcp-editor-form"]').exists()).toBe(false)
  })

  it('aligns WorkspaceRole 2/3 labels, uses *Raw fields in admin round-trip, and updates platform scope defaults', async () => {
    expect(workspaceRoleLabel(2)).toContain('TrustedMember')
    expect(workspaceRoleLabel(3)).toContain('GroupManager')

    const { client, saveSpy } = makeRbacClient({
      revision: 1,
      masked: true,
      items: [
        {
          platformId: 'feishu',
          accountScope: 'default',
          instanceId: 'feishu-main',
          scopeConversationKey: 'feishu:group:oc_***01',
          scopeConversationKeyRaw: 'feishu:group:oc_synthetic_1',
          nativeUserId: 'ou_***01',
          nativeUserIdRaw: 'ou_synthetic_mgr_1',
          role: 3,
          note: 'mgr'
        }
      ]
    })

    // Read mode renders masked labels cleanly
    const readWrapper = mount(AccessRolesCard, {
      props: { client, mode: 'read', writesEnabled: false, active: true }
    })
    mounted.push(readWrapper)
    await flushPromises()
    expect(readWrapper.get('[data-testid="rbac-readonly-list"]').text()).toContain('ou_***01')
    expect(readWrapper.get('[data-testid="rbac-readonly-list"]').text()).toContain('GroupManager')

    // Admin mode binds *Raw fields so masked placeholders are never written back
    const adminWrapper = mount(AccessRolesCard, {
      props: { client, mode: 'admin', writesEnabled: true, active: true }
    })
    mounted.push(adminWrapper)
    await flushPromises()

    expect((adminWrapper.get('[data-testid="rbac-user-0"]').element as HTMLInputElement).value).toBe('ou_synthetic_mgr_1')
    expect((adminWrapper.get('[data-testid="rbac-scope-0"]').element as HTMLInputElement).value).toBe('feishu:group:oc_synthetic_1')

    // Add second binding: default is feishu / default / feishu-main / role=3 with inline empty-scope warning
    await adminWrapper.get('[data-testid="rbac-add-btn"]').trigger('click')
    expect((adminWrapper.get('[data-testid="rbac-account-1"]').element as HTMLInputElement).value).toBe('default')
    expect((adminWrapper.get('[data-testid="rbac-instance-1"]').element as HTMLInputElement).value).toBe('feishu-main')
    expect((adminWrapper.get('[data-testid="rbac-role-1"]').element as HTMLSelectElement).value).toBe('3')
    expect(adminWrapper.find('[data-testid="rbac-scope-warning-1"]').exists()).toBe(true)

    // Switch platform to qq.private -> sets legacy / '' instead of '*'
    await adminWrapper.get('[data-testid="rbac-platform-1"]').setValue('qq.private')
    expect((adminWrapper.get('[data-testid="rbac-account-1"]').element as HTMLInputElement).value).toBe('legacy')
    expect((adminWrapper.get('[data-testid="rbac-instance-1"]').element as HTMLInputElement).value).toBe('')

    await adminWrapper.get('[data-testid="rbac-user-1"]').setValue('20001')
    await adminWrapper.get('[data-testid="rbac-scope-1"]').setValue('group:10001')
    await adminWrapper.get('[data-testid="rbac-save-btn"]').trigger('click')
    await flushPromises()

    expect(saveSpy).toHaveBeenCalledTimes(1)
    const sentBindings = (saveSpy.mock.calls[0]?.[0] as ZbaAccessRolesSaveRequest).bindings
    expect(sentBindings[0]?.nativeUserId).toBe('ou_synthetic_mgr_1')
    expect(sentBindings[0]?.scopeConversationKey).toBe('feishu:group:oc_synthetic_1')
    expect(sentBindings[1]).toMatchObject({
      platformId: 'qq.private',
      accountScope: 'legacy',
      instanceId: '',
      scopeConversationKey: 'group:10001',
      nativeUserId: '20001',
      role: 3
    })

    // qq.official & local default accountScope match runtime AccountScope.Legacy ('legacy')
    await adminWrapper.get('[data-testid="rbac-platform-1"]').setValue('qq.official')
    expect((adminWrapper.get('[data-testid="rbac-account-1"]').element as HTMLInputElement).value).toBe('legacy')
    await adminWrapper.get('[data-testid="rbac-platform-1"]').setValue('local')
    expect((adminWrapper.get('[data-testid="rbac-account-1"]').element as HTMLInputElement).value).toBe('legacy')

    // Read-only caller still renders cleanly when *Raw fields are null
    const readOnlyNullRawClient = makeRbacClient({
      revision: 2,
      masked: true,
      items: [{
        platformId: 'feishu',
        accountScope: 'default',
        instanceId: 'feishu-main',
        scopeConversationKey: 'feishu:group:oc_***01',
        scopeConversationKeyRaw: null,
        nativeUserId: 'ou_***01',
        nativeUserIdRaw: null,
        role: 3,
        note: 'o***l',
        noteRaw: null
      }]
    })
    const nullRawWrapper = mount(AccessRolesCard, {
      props: { client: readOnlyNullRawClient.client, mode: 'read', writesEnabled: false, active: true }
    })
    mounted.push(nullRawWrapper)
    await flushPromises()
    expect(nullRawWrapper.get('[data-testid="rbac-readonly-list"]').text()).toContain('ou_***01')
  })
})
