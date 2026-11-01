import type { ZbaConfigurationReceipt } from '../../api/zba'

/**
 * 「需重启」到底意味着什么，按**哪一步**需要重启分开说。
 *
 * 为什么分开：模型投影（步骤 2）需重启时，运行期路由会拒绝一切模型调用（安全静默），
 * 机器人**完全不回复**；而其它步骤需重启通常只是回读或投影需要重建。
 * 把两者都写成「需要重启后完全生效」会让用户以为还能用，实际已经哑了（#103）。
 */
export function restartNoticeMessage(
  receipt: Pick<ZbaConfigurationReceipt, 'applyState' | 'steps'> | null | undefined
): string {
  if (!receipt || receipt.applyState !== 3) return ''
  const modelWaiting = receipt.steps.some((step) => step.step === 2 && step.state === 3)
  return modelWaiting
    ? '已保存，但模型供应商路由要重启后才生效：重启前模型调用会被拒绝，机器人不会回复。首版不提供远程重启按钮。'
    : '已保存，但有步骤需要重启后完全生效；首版不提供远程重启按钮。'
}
