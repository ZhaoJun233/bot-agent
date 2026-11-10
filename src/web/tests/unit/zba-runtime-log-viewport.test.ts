import { afterEach, describe, expect, it, vi } from 'vitest'
import { captureLogAnchor, restoreLogAnchor, selectedLogText, viewportCopyText, visibleLogRows } from '../../src/features/logs/runtime-log-viewport'
function rect(top: number, height: number): DOMRect { return { top, bottom: top + height, left: 0, right: 200, width: 200, height, x: 0, y: top, toJSON() {} } }
function terminal() {
  const element = document.createElement('div')
  document.body.appendChild(element)
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue(rect(100, 100))
  for (const [index, top] of [50, 110, 150, 210].entries()) {
    const row = document.createElement('div')
    row.dataset.logId = String(index)
    row.dataset.copyText = 'safe-row-' + index
    row.textContent = row.dataset.copyText
    vi.spyOn(row, 'getBoundingClientRect').mockReturnValue(rect(top, 30))
    element.appendChild(row)
  }
  return element
}
afterEach(() => { document.body.replaceChildren(); window.getSelection()?.removeAllRanges(); vi.restoreAllMocks() })
describe('可见视口复制与阅读锚点', () => {
  it('只复制实际视口相交行，不复制上/下屏外缓冲', () => {
    const element = terminal()
    expect(viewportCopyText(element)).toBe('safe-row-1\nsafe-row-2')
    expect(visibleLogRows(element)).toHaveLength(2)
  })
  it('终端在浏览器视口外时无可复制内容', () => {
    const element = terminal()
    vi.spyOn(element, 'getBoundingClientRect').mockReturnValue(rect(window.innerHeight + 100, 200))
    expect(viewportCopyText(element)).toBe('')
  })
  it('选区只取终端内部选中文字，不捕获页面其它内容', () => {
    const element = terminal()
    const range = document.createRange()
    range.selectNodeContents(element.children[1]!)
    window.getSelection()!.addRange(range)
    expect(selectedLogText(element)).toBe('safe-row-1')
    expect(viewportCopyText(element)).toBe('safe-row-1')
    const outside = document.createElement('p'); outside.textContent = 'outside-private'; document.body.appendChild(outside)
    range.selectNodeContents(outside)
    window.getSelection()!.removeAllRanges(); window.getSelection()!.addRange(range)
    expect(viewportCopyText(element)).not.toContain('outside-private')
  })
  it('以稳定行 ID 还原锚点，淘汰后明确返回失败', () => {
    const element = terminal()
    const anchor = captureLogAnchor(element)!
    expect(anchor).toEqual({ id: '1', offset: 10 })
    vi.spyOn(element.children[1]!, 'getBoundingClientRect').mockReturnValue(rect(80, 30))
    expect(restoreLogAnchor(element, anchor)).toBe(true)
    expect(element.scrollTop).toBe(-30)
    element.children[1]!.remove()
    expect(restoreLogAnchor(element, anchor)).toBe(false)
  })
})
