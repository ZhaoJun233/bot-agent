import { afterEach, describe, expect, it } from 'vitest'
import { acquireModalIsolation, dialogFocusableElements } from '../../../src/shared/ui/modal-isolation'
import { restoreFocus } from '../../../src/shared/ui/focus-restore'

afterEach(() => document.body.replaceChildren())
describe('issue148 stacked modal isolation', () => {
  it('restores the parent when a nested modal closes, and the shell after the last close', () => {
    const shell = document.createElement('main'); const parent = document.createElement('section'); const nested = document.createElement('section')
    document.body.append(shell, parent, nested)
    const releaseParent = acquireModalIsolation(parent)
    const releaseNested = acquireModalIsolation(nested)
    expect(shell.inert).toBe(true); expect(parent.inert).toBe(true); expect(nested.inert).toBe(false)
    releaseNested(); expect(parent.inert).toBe(false); expect(shell.inert).toBe(true)
    releaseParent(); expect(shell.inert).toBe(false); expect(parent.inert).toBe(false); expect(nested.inert).toBe(false)
  })
  it('can tear down a parent before its child and releases the shell exactly once', () => {
    const shell = document.createElement('main'); const parent = document.createElement('section'); const nested = document.createElement('section')
    document.body.append(shell, parent, nested)
    const releaseParent = acquireModalIsolation(parent); const releaseNested = acquireModalIsolation(nested)
    releaseParent(); releaseParent(); expect(shell.inert).toBe(true)
    releaseNested(); expect(shell.inert).toBe(false)
  })
  it('excludes hidden descendants and uses fallback when the opener is hidden or removed', () => {
    const root = document.createElement('div')
    root.innerHTML = '<div style="display:none"><button id="hidden">隐藏</button></div><input type="hidden"><button id="fallback">返回</button>'
    document.body.append(root)
    const hidden = root.querySelector<HTMLButtonElement>('#hidden')!; const fallback = root.querySelector<HTMLButtonElement>('#fallback')!
    expect(dialogFocusableElements(root)).toEqual([fallback])
    restoreFocus(hidden, fallback); expect(document.activeElement).toBe(fallback)
    hidden.remove(); restoreFocus(hidden, fallback); expect(document.activeElement).toBe(fallback)
  })
})
