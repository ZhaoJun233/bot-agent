import { describe, expect, it } from 'vitest'
import fixture from '../fixtures/configuration-read-editable-result.json'
import { ZbaClient } from '../../src/api/zba'

function read(runtimeEffects?: unknown) {
  return new ZbaClient({ fetchImpl: async () => new Response(JSON.stringify({ ...fixture, runtimeEffects }), { headers: { 'Content-Type': 'application/json' } }) }).readConfiguration()
}
describe('可选运行时生效元数据', () => {
  it('旧服务缺失元数据保持未确认，不推导为 current', async () => {
    expect((await read()).runtimeEffects).toBeUndefined()
  })
  it('识别四种状态，只保留安全字段和明确 BotAgent 重启目标', async () => {
    const fields = ['current', 'restartRequired', 'applyFailed', 'unknown'].map(state => ({ field: { field: 101, object: null }, state, restartTarget: 'botAgent', value: 'must-not-retain' }))
    const result = await read(fields)
    expect(result.runtimeEffects?.map(x => x.state)).toEqual(['current', 'restartRequired', 'applyFailed', 'unknown'])
    expect(result.runtimeEffects?.[1]?.restartTarget).toBe('botAgent')
    expect(result.runtimeEffects?.[0]?.restartTarget).toBeUndefined()
    expect(JSON.stringify(result.runtimeEffects)).not.toContain('must-not-retain')
  })
  it.each(['current', 'restartRequired', 'applyFailed', 'unknown'] as const)('兼容服务端 WhenWritingNull 省略的标量 object：%s', async state => {
    const result = await read([{ field: { field: 101 }, state, restartTarget: 'botAgent', value: 'must-not-retain' }])
    expect(result.runtimeEffects).toEqual([{
      field: { field: 101, object: null }, state,
      ...(state === 'restartRequired' ? { restartTarget: 'botAgent' } : {})
    }])
  })
  it('规范化策略地址并剥离所有非契约值', async () => {
    const target = { kind: 'instance', key: { platformId: 'qq', accountScope: '10001', instanceId: '10001' } }
    const field = { field: 101, object: { kind: 'policy', target } }
    expect((await read([{ field: { ...field, raw: 'must-not-retain' }, state: 'current' }])).runtimeEffects).toEqual([{ field, state: 'current' }])
    expect((await read([{ field: { field: 101, object: 'invalid-address' }, state: 'current' }, { field: { field: 101, object: { kind: 'future' } }, state: 'current' }])).runtimeEffects).toEqual([])
  })
  it('不认识状态降级 unknown；畸形可选元数据不阻止设置读取', async () => {
    expect((await read([{ field: { field: 101, object: null }, state: 'future-state' }, { field: 5 }])).runtimeEffects).toEqual([{ field: { field: 101, object: null }, state: 'unknown' }])
    expect((await read({ invalid: true })).runtimeEffects).toBeUndefined()
  })
})
