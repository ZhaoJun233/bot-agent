/**
 * 模态关闭后把焦点还给「打开它的那个元素」（S4-M1）。
 *
 * 为什么需要一层判断而不是直接 focus()：
 * - 触发器可能已经被重读/权限变化销毁（isConnected === false），
 *   对离线元素 focus() 是无声空操作，焦点会掉回 body。
 * - 触发器可能还在但已经 disabled（例如写入口关闭），focus() 同样无效。
 * - 三种引擎对「点击是否给按钮焦点」的默认行为不同（Safari/WebKit 点按钮
 *   不给焦点），所以调用方要么传事件目标，要么让这里回退到页面标题。
 *
 * 这不是「尽力而为」：调用方拿到的是确定已经落位的结果，
 * 落位失败时退到 fallback，绝不让键盘用户从页面开头重新 Tab。
 */

/** 这个元素现在还能接住焦点吗。 */
export function isRestorable(element: HTMLElement | null | undefined): element is HTMLElement {
  if (!element || !element.isConnected) return false
  if (element === document.body || element === document.documentElement) return false
  if (element instanceof HTMLButtonElement && element.disabled) return false
  return true
}

/**
 * 恢复焦点：优先触发器，其次 fallback（通常是带 tabindex="-1" 的页面标题）。
 * 返回最终是否真的把焦点放到了某个元素上——调用方据此断言，不必猜。
 */
export function restoreFocus(
  trigger: HTMLElement | null | undefined,
  fallback: HTMLElement | null | undefined
): boolean {
  if (isRestorable(trigger)) {
    trigger.focus()
    if (document.activeElement === trigger) return true
  }
  if (isRestorable(fallback)) {
    fallback.focus()
    return document.activeElement === fallback
  }
  return false
}
