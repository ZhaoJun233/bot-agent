import { describe, expect, it } from 'vitest'
import type { ZbaConfigurationReceipt } from '../../src/api/zba'
import { receiptOutcome, restartNoticeMessage } from '../../src/features/configuration/receipt-notice'

function receipt(applyState: 0 | 1 | 2 | 3 | 4 | 5, steps: { step: number; state: number }[]) {
  return { applyState, steps: steps.map((item) => ({ step: item.step as never, state: item.state as never })) }
}

describe('审批回执里的“需重启”提示', () => {
  it('已全部生效时不出提示', () => {
    expect(restartNoticeMessage(receipt(1, [{ step: 2, state: 1 }]))).toBe('')
  })

  it('没有回执时不出提示', () => {
    expect(restartNoticeMessage(null)).toBe('')
  })

  it('模型投影需重启时明确说“重启前不会回复”', () => {
    const message = restartNoticeMessage(receipt(3, [{ step: 2, state: 3 }]))
    expect(message).toContain('模型连接需要重启机器人服务')
    expect(message).toContain('机器人不会回复')
  })

  it('只有其它步骤需重启时不说“不会回复”', () => {
    const message = restartNoticeMessage(receipt(3, [{ step: 2, state: 1 }, { step: 7, state: 3 }]))
    expect(message).toContain('需要重启机器人服务')
    expect(message).toContain('不需要重启 NapCat 或整台服务器')
    expect(message).not.toContain('不会回复')
  })
})

describe('receiptOutcome', () => {
  const base = (): ZbaConfigurationReceipt => ({
    key: 'synthetic-key', contractRevision: 'cfg-0.3.3', observedVersion: 'v1',
    decision: 0, commitState: 1, applyState: 1, steps: [], deviceKeyAssignments: []
  })
  it('NoChanges is neither a new save nor a failure', () => {
    const result = receiptOutcome({ ...base(), decision: 1, commitState: 0, applyState: 0 })
    expect(result.title).toBe('没有需要保存的新修改')
    expect(result.detail).toContain('原配置未改变')
    expect(result.tone).toBe('neutral')
  })
  it('unknown commit takes priority over decision and apparent apply success', () => {
    expect(receiptOutcome({ ...base(), decision: 1, commitState: 2 }).title).toBe('保存结果尚未确认')
  })
  it('changed but not committed is a failure, not runtime success', () => {
    expect(receiptOutcome({ ...base(), commitState: 0 }).title).toBe('保存失败，原配置未改变')
  })
  it('only this receipt is applied; unrelated restart debt remains separate', () => {
    const result = receiptOutcome(base())
    expect(result.title).toContain('本次修改已应用')
    expect(result.detail).toContain('其它尚待重启')
  })
  it.each([2, 4] as const)('committed apply failure %s cannot claim all changes current', applyState => {
    expect(receiptOutcome({ ...base(), applyState }).title).toContain('部分设置未应用成功')
  })
  it('restart tells the user the service and forbids automatic reboot assumptions', () => {
    const result = receiptOutcome({ ...base(), applyState: 3, steps: [{ step: 2, state: 3 }] })
    expect(result.title).toContain('需重启')
    expect(result.detail).toContain('不会自动重启')
    expect(result.detail).toContain('BotAgent')
    expect(result.detail).toContain('机器人不会回复')
    expect(result.detail).toContain('不需要重启 NapCat 或整台服务器')
  })
})
