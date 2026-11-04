import { describe, expect, it } from 'vitest'
import healthFixture from '../fixtures/health.json'
import metaFixture from '../fixtures/meta.json'
import statusFixture from '../fixtures/status.json'
import taskResultRaw from '../fixtures/task-result.json?raw'
import taskResultUnavailableRaw from '../fixtures/task-result-unavailable.json?raw'
import taskFindRaw from '../fixtures/task-find.json?raw'
import taskFindExpiredRaw from '../fixtures/task-find-expired.json?raw'
import taskCancelRaw from '../fixtures/task-cancel-result.json?raw'
import approvalSummaryRaw from '../fixtures/approval-summary.json?raw'
import approvalDecisionRaw from '../fixtures/approval-decision-result.json?raw'
import configurationFindRaw from '../fixtures/configuration-find-result.json?raw'
import taskNotFoundRaw from '../fixtures/error-task-not-found.json?raw'
import configurationReadRaw from '../fixtures/configuration-read-result.json?raw'
import configurationReadEditableRaw from '../fixtures/configuration-read-editable-result.json?raw'
import configurationSaveRaw from '../fixtures/configuration-save-result.json?raw'
import configurationConflictRaw from '../fixtures/configuration-save-conflict.json?raw'
import { evaluateCompatibility } from '../../src/api/compat'
import {
  ZbaClient,
  type ZbaApprovalDecisionResult,
  type ZbaApprovalSummary,
  type ZbaConfigurationFindResult,
  type ZbaConfigurationReadResult,
  type ZbaConfigurationSaveResult,
  type ZbaConfigurationView,
  type ZbaPlatformPolicyEntryView,
  type ZbaReadField,
  type ZbaHealth,
  type ZbaMeta,
  type ZbaStatus,
  type ZbaTaskCancelResult,
  type ZbaTaskFindResult,
  type ZbaTaskResult
} from '../../src/api/zba'
import { assertNoDuplicateKeys, assertNoNulls, assertWire, type WireShape } from './wire-guard'

const health = healthFixture as ZbaHealth
const meta = metaFixture as ZbaMeta
const status = statusFixture as ZbaStatus

function parseWire(raw: string, source: string): unknown {
  assertNoDuplicateKeys(raw, source)
  return JSON.parse(raw) as unknown
}

const componentShape: WireShape = {
  type: 'object',
  fields: {
    id: { type: 'string' },
    kind: { type: 'string' },
    displayName: { type: 'string' },
    state: { type: 'string' },
    available: { type: 'boolean' },
    reasonCode: { type: 'string' },
    updatedAt: { type: 'string' }
  },
  optional: ['reasonCode', 'updatedAt']
}

const statusShape: WireShape = {
  type: 'object',
  fields: {
    state: { type: 'string' },
    ready: { type: 'boolean' },
    version: { type: 'string' },
    startedAt: { type: 'string' },
    updatedAt: { type: 'string' },
    configurationVersion: { type: 'string' },
    configurationApplyState: { type: 'string' },
    components: { type: 'array', items: componentShape },
    recentErrors: {
      type: 'array',
      items: {
        type: 'object',
        fields: {
          at: { type: 'string' },
          scope: { type: 'string' },
          code: { type: 'string' }
        }
      }
    }
  },
  optional: ['configurationVersion']
}

const taskSummaryShape: WireShape = {
  type: 'object',
  fields: {
    id: { type: 'string' },
    state: { type: 'number' },
    target: { type: 'string' },
    sessionId: { type: 'string' },
    createdAt: { type: 'string' },
    updatedAt: { type: 'string' },
    durationMs: { type: 'number' },
    toolCalls: { type: 'number' },
    errorCode: { type: 'string' },
    cancellationRequested: { type: 'boolean' }
  },
  optional: ['durationMs', 'toolCalls', 'errorCode']
}

const taskResultShape: WireShape = {
  type: 'object',
  fields: {
    id: { type: 'string' },
    state: { type: 'number' },
    updatedAt: { type: 'string' },
    available: { type: 'boolean' },
    reasonCode: { type: 'string' },
    summary: { type: 'string' },
    summaryTruncated: { type: 'boolean' },
    summaryChars: { type: 'number' },
    toolCalls: { type: 'number' },
    durationMs: { type: 'number' },
    errorCode: { type: 'string' }
  },
  optional: ['reasonCode', 'summary', 'toolCalls', 'durationMs', 'errorCode']
}

const taskFindShape: WireShape = {
  type: 'object',
  fields: {
    code: { type: 'string' },
    instanceId: { type: 'string' },
    task: taskSummaryShape,
    taskId: { type: 'string' },
    acceptedAt: { type: 'string' }
  },
  optional: ['task', 'taskId', 'acceptedAt']
}

const approvalSummaryShape: WireShape = {
  type: 'array',
  items: {
    type: 'object',
    fields: {
      id: { type: 'string' },
      toolId: { type: 'string' },
      summary: { type: 'string' },
      session: { type: 'string' },
      expiresAt: { type: 'string' },
      policyVersion: { type: 'number' }
    }
  }
}

const fieldRuleShape: WireShape = {
  type: 'object',
  fields: {
    id: { type: 'number' },
    effect: { type: 'number' },
    canClear: { type: 'boolean' },
    editable: { type: 'boolean' },
    legacyNeedsReview: { type: 'boolean' }
  }
}

/**
 * ReadField 的 wire 形状：元数据精确冻结，`value` 的类型随字段变化，
 * 因此用 `json` 占位；哪个变体该带 `value`/`nameRaw` 由
 * `assertReadFieldVariant` 单独校验（隐藏字段不得夹带值）。
 */
const readFieldShape: WireShape = {
  type: 'object',
  fields: {
    visibility: { type: 'number' },
    display: { type: 'string' },
    rule: fieldRuleShape,
    userMode: { type: 'number' },
    effectiveSource: { type: 'number' },
    applyState: { type: 'number' },
    value: { type: 'json' },
    nameRaw: { type: 'string' }
  },
  optional: ['value', 'nameRaw']
}

const policyTargetShape: WireShape = {
  type: 'object',
  fields: {
    kind: { type: 'string' },
    key: {
      type: 'object',
      fields: {
        platformId: { type: 'string' },
        accountScope: { type: 'string' },
        instanceId: { type: 'string' }
      }
    },
    platformId: { type: 'string' },
    accountScope: { type: 'string' }
  },
  optional: ['key', 'platformId', 'accountScope']
}

const policyEntryShape: WireShape = {
  type: 'object',
  fields: {
    target: policyTargetShape,
    enabled: readFieldShape,
    chatEnabled: readFieldShape,
    groupWhitelist: readFieldShape,
    privateWhitelist: readFieldShape,
    allowedActions: readFieldShape,
    features: readFieldShape
  }
}

const pluginShape: WireShape = {
  type: 'object',
  fields: {
    key: { type: 'string' },
    desiredEnabled: { type: 'boolean' }
  }
}

const providerShape: WireShape = {
  type: 'object',
  fields: {
    key: { type: 'string' },
    name: readFieldShape,
    baseUrl: readFieldShape,
    modelName: readFieldShape,
    priority: { type: 'number' },
    desiredEnabled: { type: 'boolean' },
    credential: { type: 'number' },
    runtimeApplyState: { type: 'number' }
  }
}

const configurationViewShape: WireShape = {
  type: 'object',
  fields: {
    behavior: { type: 'record', values: readFieldShape, min: 1 },
    model: { type: 'record', values: readFieldShape, min: 1 },
    agent: { type: 'record', values: readFieldShape, min: 1 },
    platform: { type: 'record', values: readFieldShape, min: 1 },
    voice: { type: 'record', values: readFieldShape, min: 1 },
    music: { type: 'record', values: readFieldShape, min: 1 },
    reporting: { type: 'record', values: readFieldShape, min: 1 },
    registered: {
      type: 'object',
      fields: {
        plugins: { type: 'array', items: pluginShape },
        providers: { type: 'array', items: providerShape }
      }
    }
  }
}

const configurationReadShape: WireShape = {
  type: 'object',
  fields: {
    code: { type: 'number' },
    version: { type: 'string' },
    generation: { type: 'string' },
    contractRevision: { type: 'string' },
    serverUnixSeconds: { type: 'number' },
    view: configurationViewShape,
    secrets: {
      type: 'array',
      items: {
        type: 'object',
        fields: {
          slot: { type: 'number' },
          configured: { type: 'boolean' },
          source: { type: 'number' }
        }
      }
    },
    fields: {
      type: 'array',
      items: {
        type: 'object',
        fields: {
          id: { type: 'number' },
          effect: { type: 'number' },
          canClear: { type: 'boolean' },
          editable: { type: 'boolean' },
          legacyNeedsReview: { type: 'boolean' }
        }
      }
    },
    latestApplyState: { type: 'number' }
  },
  optional: ['view']
}

const applyStepShape: WireShape = {
  type: 'object',
  fields: {
    step: { type: 'number' },
    state: { type: 'number' },
    errorCode: { type: 'string' }
  },
  optional: ['errorCode']
}

const receiptShape: WireShape = {
  type: 'object',
  fields: {
    key: { type: 'string' },
    contractRevision: { type: 'string' },
    decision: { type: 'number' },
    observedVersion: { type: 'string' },
    committedVersion: { type: 'string' },
    commitState: { type: 'number' },
    applyState: { type: 'number' },
    steps: { type: 'array', items: applyStepShape },
    deviceKeyAssignments: {
      type: 'array',
      items: {
        type: 'object',
        fields: {
          inputOrdinal: { type: 'number' },
          key: { type: 'string' }
        }
      }
    }
  },
  optional: ['committedVersion']
}

const configurationSaveShape: WireShape = {
  type: 'object',
  fields: {
    code: { type: 'number' },
    replayed: { type: 'boolean' },
    receipt: receiptShape,
    currentVersion: { type: 'string' },
    errors: { type: 'array', items: { type: 'object', fields: {} } }
  },
  optional: ['receipt']
}

const READ_SECTIONS = ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting'] as const
const POLICY_FIELD_NAMES = [
  'enabled',
  'chatEnabled',
  'groupWhitelist',
  'privateWhitelist',
  'allowedActions',
  'features'
] as const

/**
 * ReadField 的 visibility 与值字段一一对应：0 隐藏不带值，1 必须带 `value`，
 * 2 必须带 `nameRaw`。形状校验通过后再校验这个语义约束。
 */
function assertReadFieldVariant(field: unknown, path = 'field'): void {
  assertWire(field, readFieldShape, path)
  const candidate = field as ZbaReadField
  const hasValue = Object.prototype.hasOwnProperty.call(candidate, 'value')
  const hasNameRaw = Object.prototype.hasOwnProperty.call(candidate, 'nameRaw')
  switch (candidate.visibility) {
    case 0:
      if (hasValue || hasNameRaw) throw new Error(`${path}: hidden field must not carry value/nameRaw`)
      return
    case 1:
      if (!hasValue) throw new Error(`${path}: editable field must carry value`)
      if (hasNameRaw) throw new Error(`${path}: editable field must not carry nameRaw`)
      return
    case 2:
      if (!hasNameRaw) throw new Error(`${path}: nameEditable field must carry nameRaw`)
      if (hasValue) throw new Error(`${path}: nameEditable field must not carry value`)
      return
    default:
      throw new Error(`${path}: unknown visibility ${String(candidate.visibility)}`)
  }
}

function assertViewFieldVariants(view: ZbaConfigurationView): void {
  for (const section of READ_SECTIONS) {
    const fields = view[section] as Record<string, ZbaReadField>
    for (const [name, field] of Object.entries(fields)) {
      assertReadFieldVariant(field, `${section}.${name}`)
    }
  }
  for (const [index, entry] of (view.platform.platformPolicies.value ?? []).entries()) {
    for (const name of POLICY_FIELD_NAMES) {
      assertReadFieldVariant(entry[name], `platformPolicies[${index}].${name}`)
    }
  }
  for (const provider of view.registered.providers) {
    assertReadFieldVariant(provider.name, `providers.${provider.key}.name`)
    assertReadFieldVariant(provider.baseUrl, `providers.${provider.key}.baseUrl`)
    assertReadFieldVariant(provider.modelName, `providers.${provider.key}.modelName`)
  }
}

describe('冻结 wire 样例', () => {
  it('health 字段集合与 zba-1.0 一致', () => {
    expect(Object.keys(healthFixture).sort()).toEqual([
      'checkedAt',
      'configured',
      'contractVersion',
      'ready',
      'status'
    ])
    expect(healthFixture.ready).toBeTypeOf('boolean')
  })

  it('meta 字段集合包含 instanceId/features，且 feature 列表冻结', () => {
    expect(Object.keys(metaFixture).sort()).toEqual([
      'authentication',
      'contractVersion',
      'dockerManagement',
      'features',
      'instanceId',
      'maxBodyBytes',
      'mcpManagement',
      'scopes',
      'serverVersion',
      'sse'
    ])
    expect(meta.features).toEqual([
      'sessions.list',
      'sessions.ensure',
      'tasks.result',
      'tasks.find',
      'tasks.receipts'
    ])
  })

  it('status 顶层与 components/recentErrors 嵌套字段精确冻结', () => {
    assertWire(statusFixture, statusShape)
    assertNoNulls(statusFixture)
    expect(Object.keys(statusFixture).sort()).toEqual([
      'components',
      'configurationApplyState',
      'configurationVersion',
      'ready',
      'recentErrors',
      'startedAt',
      'state',
      'updatedAt',
      'version'
    ])
    expect(JSON.stringify(statusFixture)).not.toMatch(/prompt|summary|result|message/i)
  })

  it('冻结样例可被 client 接受，并判定为兼容', () => {
    expect(evaluateCompatibility(meta, health).compatible).toBe(true)

    const fetchImpl = (async (input: RequestInfo | URL) => {
      const url = String(input)
      const body = url.endsWith('/health') ? healthFixture
        : url.endsWith('/meta') ? metaFixture
          : statusFixture
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      })
    }) as typeof fetch

    const client = new ZbaClient({ token: 'synthetic-token', fetchImpl })
    return expect(Promise.all([client.health(), client.meta(), client.status()])).resolves.toEqual([
      health,
      meta,
      status
    ])
  })

  it('任务结果：枚举按数字，null 字段省略，成功/未完成分支分开', () => {
    const success = parseWire(taskResultRaw, 'task-result.json') as ZbaTaskResult
    const unavailable = parseWire(taskResultUnavailableRaw, 'task-result-unavailable.json') as ZbaTaskResult

    assertWire(success, taskResultShape)
    assertWire(unavailable, taskResultShape)
    assertNoNulls(success)
    assertNoNulls(unavailable)

    expect(success.state).toBe(3)
    expect(success.state).toBeTypeOf('number')
    expect(success.reasonCode).toBeUndefined()
    expect(success.errorCode).toBeUndefined()
    expect(unavailable.state).toBe(1)
    expect(unavailable.available).toBe(false)
    expect(unavailable.reasonCode).toBe('not_terminal')
    expect(unavailable.summary).toBeUndefined()
    expect(unavailable.durationMs).toBeUndefined()
  })

  it('按幂等键 find：found 带嵌套任务摘要，expired 只带受理信息', () => {
    const found = parseWire(taskFindRaw, 'task-find.json') as ZbaTaskFindResult
    const expired = parseWire(taskFindExpiredRaw, 'task-find-expired.json') as ZbaTaskFindResult

    assertWire(found, taskFindShape)
    assertWire(expired, taskFindShape)
    assertNoNulls(found)
    assertNoNulls(expired)
    expect(found.code).toBe('found')
    expect(found.task?.state).toBe(3)
    expect(found.taskId).toBeUndefined()
    expect(expired.code).toBe('expired')
    expect(expired.task).toBeUndefined()
    expect(expired.taskId).toBe('task-synthetic-done')
  })

  it('审批列表只含安全摘要，不出现 taskId 猜关联', () => {
    const approvals = parseWire(approvalSummaryRaw, 'approval-summary.json') as ZbaApprovalSummary[]
    assertWire(approvals, approvalSummaryShape)
    assertNoNulls(approvals)
    expect(approvals).toHaveLength(1)
    expect(approvals[0]?.policyVersion).toBeTypeOf('number')
    expect(JSON.stringify(approvals)).not.toMatch(/prompt|result|taskId/i)
  })

  it('取消/审批决定/config find：拒绝态不伪装成功，receipt 可复用', () => {
    const cancel = parseWire(taskCancelRaw, 'task-cancel-result.json') as ZbaTaskCancelResult
    const decision = parseWire(approvalDecisionRaw, 'approval-decision-result.json') as ZbaApprovalDecisionResult
    const find = parseWire(configurationFindRaw, 'configuration-find-result.json') as ZbaConfigurationFindResult

    assertWire(cancel, {
      type: 'object',
      fields: {
        state: { type: 'number' },
        cancellationRequested: { type: 'boolean' },
        outcome: { type: 'string' }
      }
    })
    assertWire(decision, {
      type: 'object',
      fields: {
        ok: { type: 'boolean' },
        decision: { type: 'string' },
        reasonCode: { type: 'string' }
      }
    })
    assertWire(find, {
      type: 'object',
      fields: { code: { type: 'number' }, receipt: receiptShape },
      optional: ['receipt']
    })
    assertNoNulls(cancel)
    assertNoNulls(decision)
    assertNoNulls(find)

    expect(cancel.state).toBe(6)
    expect(cancel.cancellationRequested).toBe(true)
    expect(decision.ok).toBe(true)
    expect(find.receipt?.contractRevision).toBe('cfg-0.3.3')
  })

  it('配置 read/保存结果：版本是字符串，状态/枚举报文数字，receipt 嵌套字段冻结', () => {
    const read = parseWire(configurationReadRaw, 'configuration-read-result.json') as ZbaConfigurationReadResult
    const saved = parseWire(configurationSaveRaw, 'configuration-save-result.json') as ZbaConfigurationSaveResult
    const conflict = parseWire(configurationConflictRaw, 'configuration-save-conflict.json') as ZbaConfigurationSaveResult

    assertWire(read, configurationReadShape)
    assertWire(saved, configurationSaveShape)
    assertWire(conflict, configurationSaveShape)
    assertNoNulls(read)
    assertNoNulls(saved)
    assertNoNulls(conflict)

    expect(read.version).toBe('1')
    expect(read.generation).toMatch(/^[0-9a-f]{32}$/)
    // 配置快照的 contractRevision 是 cfg-*，不是 ZBA 的 zba-1.0。
    expect(read.contractRevision).toBe('cfg-0.3.3')
    expect(read.view).toBeDefined()
    expect(read.latestApplyState).toBe(0)
    expect(saved.code).toBe(0)
    expect(saved.receipt?.contractRevision).toBe('cfg-0.3.3')
    expect(saved.receipt?.decision).toBe(0)
    expect(saved.receipt?.applyState).toBe(1)
    expect(saved.receipt?.committedVersion).toBe('8')
    expect(conflict.code).toBe(3)
    expect(conflict.receipt).toBeUndefined()
  })

  it('配置 read 非空 view：8 个分区、隐藏字段不带值、秘密只有元数据', () => {
    const read = parseWire(configurationReadRaw, 'configuration-read-result.json') as ZbaConfigurationReadResult
    assertWire(read, configurationReadShape)
    assertNoNulls(read)
    const view = read.view as ZbaConfigurationView

    expect(Object.keys(view)).toEqual([...READ_SECTIONS, 'registered'])
    for (const section of READ_SECTIONS) {
      expect(Object.keys(view[section]).length).toBeGreaterThan(0)
    }
    assertViewFieldVariants(view)
    expect(view.registered.plugins).toHaveLength(2)
    expect(view.registered.providers).toHaveLength(1)
    expect(read.secrets).toHaveLength(6)
    expect(read.fields.length).toBeGreaterThanOrEqual(9)
    // 秘密只以 {slot,configured,source} 出现，样例里没有任何秘密值。
    for (const secret of read.secrets) {
      expect(Object.keys(secret).sort()).toEqual(['configured', 'slot', 'source'])
    }
    expect(JSON.stringify(read)).not.toMatch(/"(secretValue|plainText|password)"\s*:/i)
  })

  it('配置 read 可编辑样例：value 只在可见字段出现，策略与供应商嵌套形状冻结', () => {
    const read = parseWire(
      configurationReadEditableRaw,
      'configuration-read-editable-result.json'
    ) as ZbaConfigurationReadResult
    assertWire(read, configurationReadShape)
    assertNoNulls(read)
    const view = read.view as ZbaConfigurationView

    for (const section of READ_SECTIONS) {
      for (const field of Object.values(view[section] as Record<string, ZbaReadField>)) {
        expect(field.visibility).toBe(1)
      }
    }
    assertViewFieldVariants(view)

    const policies = view.platform.platformPolicies
    expect(policies.visibility).toBe(1)
    expect(policies.value).toHaveLength(1)
    const entry = policies.value?.[0] as ZbaPlatformPolicyEntryView
    assertWire(entry, policyEntryShape)
    expect(entry.target.kind).toBe('instance')
    expect(entry.allowedActions.value).toEqual({ kind: 1, actions: ['action-sample'] })
    expect(entry.features.value).toEqual([{ feature: 0, enabled: true }])

    const provider = view.registered.providers[0]
    if (provider === undefined) throw new Error('editable fixture is missing a provider')
    expect(provider.credential).toBe(1)
    expect(provider.name.value).toBe('sample-provider')
    expect(provider.baseUrl.value).toBe('https://example.invalid/v1')
    expect(JSON.stringify(view)).not.toMatch(/"(secretValue|plainText|password)"\s*:/i)
  })

  it('ReadField 变体与秘密元数据的负例必须被拒绝', () => {
    const rule = { id: 1, effect: 0, canClear: true, editable: true, legacyNeedsReview: false }
    const base = { display: '***', rule, userMode: 0, effectiveSource: 0, applyState: 0 }

    expect(() => assertReadFieldVariant({ ...base, visibility: 0, value: 'x' })).toThrow(/hidden field/)
    expect(() => assertReadFieldVariant({ ...base, visibility: 1 })).toThrow(/must carry value/)
    expect(() => assertReadFieldVariant({ ...base, visibility: 2, value: 'x' })).toThrow(/nameRaw/)
    expect(() => assertReadFieldVariant({ ...base, visibility: 2, nameRaw: 'x' })).not.toThrow()
    expect(() => assertWire({ ...base, visibility: 0, secretValue: 'x' }, readFieldShape))
      .toThrow(/unknown field/)
    expect(() => assertWire({ slot: 0, configured: true, source: 1, value: 'x' }, {
      type: 'object',
      fields: {
        slot: { type: 'number' },
        configured: { type: 'boolean' },
        source: { type: 'number' }
      }
    })).toThrow(/unknown field/)

    const read = parseWire(configurationReadRaw, 'configuration-read-result.json') as ZbaConfigurationReadResult
    expect(() => assertWire({ ...read.view, unexpected: {} }, configurationViewShape))
      .toThrow(/unknown field/)
    const { behavior: _behavior, ...withoutBehavior } = read.view as ZbaConfigurationView
    expect(() => assertWire(withoutBehavior, configurationViewShape)).toThrow(/missing field/)
    expect(() => assertNoNulls({ value: null })).toThrow(/null/)
  })

  it('错误样例省略 null 的 reason/backendCode，且不夹带私有字段', () => {
    const error = parseWire(taskNotFoundRaw, 'error-task-not-found.json')
    assertWire(error, {
      type: 'object',
      fields: {
        error: {
          type: 'object',
          fields: {
            code: { type: 'string' },
            message: { type: 'string' },
            reason: { type: 'string' },
            backendCode: { type: 'number' }
          },
          optional: ['reason', 'backendCode']
        }
      }
    })
    assertNoNulls(error)
    expect(JSON.stringify(error)).not.toMatch(/detail|prompt|authorization/i)
  })


  /**
   * S3 契约回归：真实宿主的三个 decoder 缺口。
   *
   * 这三条都是"用真宿主返回值作契约回归"——之前用信封/显式 null 的样例测过，
   * 却没测真实 wire，于是 S3-02/S3-03 一路绿测到验收才暴露。
   */
  describe('S3 真实 wire 回归', () => {
    function clientWithBody(body: unknown, status = 200): ZbaClient {
      return new ZbaClient({
        fetchImpl: async () => new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' }
        })
      })
    }

    it('任务末页省略 nextCursor（JsonIgnoreCondition 写 null 为省略）仍能解码', async () => {
      const client = clientWithBody({
        items: [{
          id: 'zt-1', state: 3, target: 'server', sessionId: 'synthetic-session',
          createdAt: '2026-10-23T00:00:00Z', updatedAt: '2026-10-23T00:00:01Z',
          cancellationRequested: false
        }]
      })
      const page = await client.listTasks({ limit: 50 })
      expect(page.items).toHaveLength(1)
      expect(page.nextCursor ?? null).toBeNull()
    })

    it('会话末页同样省略 nextCursor', async () => {
      const client = clientWithBody({
        items: [{
          id: 's-1', name: 'synthetic', backend: 'server', current: true, turns: 0,
          historyChars: 0, createdAt: '2026-10-23T00:00:00Z', updatedAt: '2026-10-23T00:00:00Z'
        }]
      })
      const page = await client.listSessions('panel:synthetic')
      expect(page.items).toHaveLength(1)
      expect(page.nextCursor ?? null).toBeNull()
    })

    it('审批队列是裸数组（不是 {items:[]} 信封）', async () => {
      const client = clientWithBody([{
        id: 'a-1', toolId: 'synthetic.tool', summary: '合成摘要',
        session: 'synthetic-session', expiresAt: '2026-10-23T01:00:00Z', policyVersion: 1
      }])
      const approvals = await client.listApprovals()
      expect(approvals).toHaveLength(1)
      expect(approvals[0]?.id).toBe('a-1')
    })

    it('审批队列为空数组时返回空而不是报错', async () => {
      const client = clientWithBody([])
      await expect(client.listApprovals()).resolves.toEqual([])
    })

    it('审批队列格式无效时抛错，不会被当成"确认没有审批"', async () => {
      const client = clientWithBody({ wrong: 'shape' })
      await expect(client.listApprovals()).rejects.toMatchObject({ code: 'invalid_json' })
    })

    it('found 必须带完整任务摘要：畸形 task.id=42 不能升级成已受理', async () => {
      const client = clientWithBody({
        code: 'found', instanceId: 'synthetic-instance', task: { id: 42 }, taskId: null, acceptedAt: null
      })
      await expect(client.findTask('task1-x')).rejects.toMatchObject({ code: 'invalid_json' })
    })

    it('expired 必须给受理时的 taskId，缺失即无效', async () => {
      const missing = clientWithBody({
        code: 'expired', instanceId: 'synthetic-instance', task: null, taskId: null, acceptedAt: null
      })
      await expect(missing.findTask('task1-x')).rejects.toMatchObject({ code: 'invalid_json' })

      const ok = clientWithBody({
        code: 'expired', instanceId: 'synthetic-instance', task: null,
        taskId: 'zt-9', acceptedAt: '2026-10-23T00:00:00Z'
      })
      await expect(ok.findTask('task1-x')).resolves.toMatchObject({ code: 'expired', taskId: 'zt-9' })
    })

    it('事件分页可解码，resyncRequired 与锚点原样保留', async () => {
      const client = clientWithBody({
        items: [{ sequence: 4, type: 'task.updated', at: '2026-10-23T00:00:00Z', taskId: 'zt-1', state: 'running' }],
        nextSequence: 4, resyncRequired: false, generatedAt: '2026-10-23T00:00:01Z'
      })
      const page = await client.listEvents(3, 100)
      expect(page.nextSequence).toBe(4)
      expect(page.resyncRequired).toBe(false)
      expect(page.items[0]?.taskId).toBe('zt-1')
    })

    // ── R5：202 + 空任务 id 不是稳定身份，不能当受理成功 ──────────────
    it('202 回执的 id 为空字符串时必须判为非法响应，不能当受理成功', async () => {
      const client = clientWithBody({ id: '', state: 0, acceptedAt: '2026-10-23T00:00:00Z' }, 202)
      await expect(client.submitTask({
        sourceKey: 'panel:synthetic', sessionId: 'synthetic-session',
        prompt: '合成任务', target: 'server', timeoutSeconds: 120
      }, 'task1-synthetic-key')).rejects.toMatchObject({ code: 'invalid_json' })
    })

    it('稳定 id 的 202 回执仍正常解码', async () => {
      const client = clientWithBody({ id: 'zt-1', state: 0, acceptedAt: '2026-10-23T00:00:00Z' }, 202)
      await expect(client.submitTask({
        sourceKey: 'panel:synthetic', sessionId: 'synthetic-session',
        prompt: '合成任务', target: 'server', timeoutSeconds: 120
      }, 'task1-synthetic-key')).resolves.toMatchObject({ id: 'zt-1', state: 0 })
    })

    it('5xx 错误体即使长得像成功回包也不能算受理', async () => {
      const client = clientWithBody({ id: 'zt-1', state: 0, acceptedAt: '2026-10-23T00:00:00Z' }, 502)
      await expect(client.submitTask({
        sourceKey: 'panel:synthetic', sessionId: 'synthetic-session',
        prompt: '合成任务', target: 'server', timeoutSeconds: 120
      }, 'task1-synthetic-key')).rejects.toMatchObject({ status: 502 })
    })
  })

  it('契约守卫拒绝未知字段、缺失字段与任意层级的重复 key', () => {
    expect(() => assertWire({ ...statusFixture, extra: true }, statusShape)).toThrow(/unknown field/)
    const { state: _state, ...withoutState } = statusFixture
    expect(() => assertWire(withoutState, statusShape)).toThrow(/missing field/)
    expect(() => assertNoDuplicateKeys('{"kind":0,"kind":1}')).toThrow(/duplicate key/)
    expect(() => assertNoDuplicateKeys('{"outer":{"inner":1,"inner":2}}')).toThrow(/duplicate key/)
    expect(() => assertNoDuplicateKeys('{"kind":0,"nested":[{"a":1,"a":2}]}')).toThrow(/duplicate key/)
  })
})
