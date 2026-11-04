import { describe, expect, it } from 'vitest'
import { restartNoticeMessage } from '../../src/features/configuration/receipt-notice'

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
    expect(message).toContain('模型供应商路由')
    expect(message).toContain('机器人不会回复')
  })

  it('只有其它步骤需重启时不说“不会回复”', () => {
    const message = restartNoticeMessage(receipt(3, [{ step: 2, state: 1 }, { step: 7, state: 3 }]))
    expect(message).toContain('重启后完全生效')
    expect(message).not.toContain('不会回复')
  })
})
