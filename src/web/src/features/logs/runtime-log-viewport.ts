/** Only rows intersecting the terminal AND browser viewport are eligible. */
export function visibleLogRows(terminal: HTMLElement): HTMLElement[] {
  const bounds = terminal.getBoundingClientRect()
  const top = Math.max(0, bounds.top)
  const bottom = Math.min(window.innerHeight, bounds.bottom)
  if (bottom <= top || bounds.right <= 0 || bounds.left >= window.innerWidth) return []
  return Array.from(terminal.querySelectorAll<HTMLElement>('[data-log-id]')).filter(row => {
    const rect = row.getBoundingClientRect()
    return rect.bottom > top && rect.top < bottom && rect.height > 0
  })
}

export function selectedLogText(terminal: HTMLElement): string {
  const selection = window.getSelection()
  if (!selection || selection.isCollapsed || !selection.anchorNode || !selection.focusNode
    || !terminal.contains(selection.anchorNode) || !terminal.contains(selection.focusNode)) return ''
  return selection.toString()
}

export function viewportCopyText(terminal: HTMLElement): string {
  const selected = selectedLogText(terminal)
  if (selected) return selected
  return visibleLogRows(terminal).map(row => row.dataset.copyText ?? '').filter(Boolean).join('\n')
}

export interface LogAnchor { id: string; offset: number }
export function captureLogAnchor(terminal: HTMLElement): LogAnchor | undefined {
  const first = visibleLogRows(terminal)[0]
  return first?.dataset.logId ? { id: first.dataset.logId, offset: first.getBoundingClientRect().top - terminal.getBoundingClientRect().top } : undefined
}

export function restoreLogAnchor(terminal: HTMLElement, anchor: LogAnchor): boolean {
  const row = Array.from(terminal.querySelectorAll<HTMLElement>('[data-log-id]')).find(item => item.dataset.logId === anchor.id)
  if (!row) return false
  terminal.scrollTop += row.getBoundingClientRect().top - terminal.getBoundingClientRect().top - anchor.offset
  return true
}
