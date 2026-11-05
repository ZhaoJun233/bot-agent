import type { ZbaConfigurationReceipt } from '../../api/zba'

/**
 * 「需重启」到底意味着什么，按**哪一步**需要重启分开说。
 *
 * 为什么分开：模型投影（步骤 2）需重启时，运行期路由会拒绝一切模型调用（安全静默），
 * 机器人**完全不回复**；而其它步骤需重启通常只是回读或投影需要重建。
 * 把两者都写成「需要重启后完全生效」会让用户以为还能用，实际已经哑了（#103）。
 */
export function receiptOutcome(receipt: ZbaConfigurationReceipt): { title: string; detail: string; tone: string } {
  if (receipt.commitState === 2) return { title:'保存结果尚未确认', detail:'请核对本次结果后再操作，草稿会保留。', tone:'warn' }
  if (receipt.decision === 1 && receipt.commitState === 0) return { title:'没有需要保存的新修改', detail:'原配置未改变；当前运行状态以重新读取的状态为准。', tone:'neutral' }
  if (receipt.commitState !== 1) return { title:'保存失败，原配置未改变', detail:'请检查提示并修改草稿，再保存。', tone:'bad' }
  if (receipt.applyState === 1) return { title:'配置已保存，本次修改已应用', detail:'请查看各项的当前生效情况；其它尚待重启的设置可能仍未生效。', tone:'ok' }
  if (receipt.applyState === 3) return { title:'配置已保存，需重启', detail:restartNoticeMessage(receipt), tone:'warn' }
  if (receipt.applyState === 4 || receipt.applyState === 2) return { title:'配置已保存，部分设置未应用成功', detail:'请检查失败项并重新读取当前状态；不要重复提交相同修改。', tone:'bad' }
  return { title:'配置已保存，生效情况待确认', detail:'请重新读取并查看各项的当前生效情况。', tone:'warn' }
}

export function restartNoticeMessage(
  receipt: Pick<ZbaConfigurationReceipt, 'applyState' | 'steps'> | null | undefined
): string {
  if (!receipt || receipt.applyState !== 3) return ''
  const modelWaiting = receipt.steps.some((step) => step.step === 2 && step.state === 3)
  return modelWaiting
    ? '配置已保存，但模型连接需要重启机器人服务后才能恢复；重启前模型调用会被拒绝，机器人不会回复。不会自动重启，请由运维重启 BotAgent 服务，再重新读取确认；不需要重启 NapCat 或整台服务器。'
    : '配置已保存，部分设置需要重启机器人服务。不会自动重启，请由运维重启 BotAgent 服务，再重新读取确认；不需要重启 NapCat 或整台服务器。'
}
