import type { ZbaTaskFindCode } from '../../api/zba'

// Missing records never prove that the original task did not execute.
export const taskFindCodeLabels: Record<ZbaTaskFindCode, string> = {
  found: '已找到受理记录',
  expired: '持有受理回执，任务记录已被清理',
  accepted_unknown: '已受理但结果未知（可能来自先前实例）',
  unknown: '查不到受理记录（这不等于任务没有执行）'
}
