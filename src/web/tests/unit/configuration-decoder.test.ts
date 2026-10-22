import { describe, expect, it } from 'vitest'
import editableFixture from '../fixtures/configuration-read-editable-result.json'
import { ZbaClient } from '../../src/api/zba'

type Json = Record<string, unknown>

function clone(): Json {
  return structuredClone(editableFixture) as unknown as Json
}

function clientReturning(payload: unknown, calls: Array<{ url: string; init: RequestInit }> = []): ZbaClient {
  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), init: init ?? {} })
    return new Response(JSON.stringify(payload), {
      status: 200,
      headers: { 'content-type': 'application/json' }
    })
  }) as unknown as typeof fetch
  return new ZbaClient({ token: 'synthetic-web-read-token', fetchImpl })
}

function viewOf(payload: Json): Json {
  return payload.view as Json
}

describe('配置 read 运行时解码', () => {
  it('接受可编辑样例并把 kind 发到 read 端点', async () => {
    const calls: Array<{ url: string; init: RequestInit }> = []
    const result = await clientReturning(editableFixture, calls).readConfiguration(1)

    expect(result.view?.behavior.botPersona?.value).toBe('示例人格')
    expect(result.secrets).toHaveLength(6)
    expect(calls).toHaveLength(1)
    expect(calls[0]?.url).toBe('/zba/v1/configuration/read')
    expect(calls[0]?.init.method).toBe('POST')
    expect(calls[0]?.init.body).toBe('{"kind":1}')
  })

  it('view 省略（概览投影）仍然有效', async () => {
    const payload = clone()
    delete payload.view
    const result = await clientReturning(payload).readConfiguration(0)
    expect(result.view).toBeUndefined()
  })

  it('隐藏字段夹带 value 时按 invalid_json 拒绝', async () => {
    const payload = clone()
    const behavior = viewOf(payload).behavior as Record<string, Json>
    behavior.botPersona = { ...behavior.botPersona, visibility: 0 }
    await expect(clientReturning(payload).readConfiguration(1)).rejects.toMatchObject({ code: 'invalid_json' })
  })

  it('可编辑字段缺 value 时按 invalid_json 拒绝', async () => {
    const payload = clone()
    const behavior = viewOf(payload).behavior as Record<string, Json>
    const { value: _value, ...withoutValue } = behavior.botPersona!
    behavior.botPersona = withoutValue
    await expect(clientReturning(payload).readConfiguration(1)).rejects.toMatchObject({ code: 'invalid_json' })
  })

  it('未知 visibility 与缺失分区都按 invalid_json 拒绝', async () => {
    const unknownVisibility = clone()
    const behavior = viewOf(unknownVisibility).behavior as Record<string, Json>
    behavior.botPersona = { ...behavior.botPersona, visibility: 3 }
    await expect(clientReturning(unknownVisibility).readConfiguration(1))
      .rejects.toMatchObject({ code: 'invalid_json' })

    const missingSection = clone()
    delete viewOf(missingSection).model
    await expect(clientReturning(missingSection).readConfiguration(1))
      .rejects.toMatchObject({ code: 'invalid_json' })
  })
})
