import { onScopeDispose, watch, type Ref } from 'vue'

const dialogs: HTMLElement[] = []
const originalInert = new Map<HTMLElement, boolean>()
let originalOverflow = ''

function restoreBackground(): void {
  for (const [element, inert] of originalInert) element.inert = inert
  originalInert.clear()
}

function isolateTopDialog(): void {
  restoreBackground()
  let branch = dialogs.at(-1)
  while (branch?.parentElement) {
    for (const sibling of branch.parentElement.children) {
      if (sibling instanceof HTMLElement && sibling !== branch) {
        originalInert.set(sibling, sibling.inert)
        sibling.inert = true
      }
    }
    if (branch.parentElement === document.body) break
    branch = branch.parentElement
  }
}

/** Isolate the top modal only; nested confirmations restore their parent on close. */
export function acquireModalIsolation(dialog: HTMLElement): () => void {
  if (dialogs.length === 0) {
    originalOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
  }
  dialogs.push(dialog)
  isolateTopDialog()
  let released = false
  return () => {
    if (released) return
    released = true
    const index = dialogs.indexOf(dialog)
    if (index >= 0) dialogs.splice(index, 1)
    isolateTopDialog()
    if (dialogs.length === 0) document.body.style.overflow = originalOverflow
  }
}

export function useModalIsolation(root: Ref<HTMLElement | null>, active: () => boolean): void {
  let release: (() => void) | undefined
  watch([root, active], ([element, visible]) => {
    release?.()
    release = element && visible ? acquireModalIsolation(element) : undefined
  }, { immediate: true, flush: 'post' })
  onScopeDispose(() => release?.())
}

export function dialogFocusableElements(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(
    'button:not(:disabled), input:not(:disabled):not([type="hidden"]), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]'
  )).filter((element) => {
    if (element.closest('[hidden], [inert], [aria-hidden="true"]')) return false
    let ancestor: HTMLElement | null = element
    while (ancestor) {
      const style = getComputedStyle(ancestor)
      if (style.display === 'none' || style.visibility === 'hidden') return false
      if (ancestor === root) break
      ancestor = ancestor.parentElement
    }
    return true
  })
}
