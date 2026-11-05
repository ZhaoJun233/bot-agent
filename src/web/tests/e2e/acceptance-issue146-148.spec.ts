import { expect, test, type Page, type TestInfo } from '@playwright/test'
import { writeFile } from 'node:fs/promises'

const pages = [
  ['overview', '概览', 'overview'], ['capabilities', '能力', 'capabilities'],
  ['configuration', '配置', 'configuration'], ['tasks', '任务', 'tasks'],
  ['traces', '诊断', 'traces'], ['logs', '运行日志', 'runtime-logs-page'],
] as const

async function connect(page: Page, theme: string, info: TestInfo): Promise<void> {
  await page.addInitScript(value => localStorage.setItem('botagent-appearance', value), theme)
  await page.goto('/')
  await expect(page.getByTestId('token-input')).toBeVisible()
  await capture(page, info, `${theme}-${page.viewportSize()!.width}-authentication`)
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}

async function capture(page: Page, info: TestInfo, label: string) {
  await page.screenshot({ path: info.outputPath(label + '.png'), fullPage: true, animations: 'disabled' })
  const data = await page.evaluate(() => {
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect()
      return !element.closest('[inert]') && rect.width > 0 && rect.height > 0 && getComputedStyle(element).visibility !== 'hidden'
    }
    const color = (value: string): number[] => {
      const values = value.match(/[\d.]+/g)?.map(Number) ?? [0, 0, 0, 0]
      return [values[0]!, values[1]!, values[2]!, values[3] ?? 1]
    }
    const blend = (fg: number[], bg: number[]) => [0, 1, 2].map(index => fg[index]! * fg[3]! + bg[index]! * (1 - fg[3]!))
    const luminance = (rgb: number[]) => rgb.slice(0, 3).map(v => {
      const s = v / 255
      return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
    }).reduce((sum, v, i) => sum + v * [0.2126, 0.7152, 0.0722][i]!, 0)
    const contrastRatio = (foreground: number[], background: number[]) => {
      const a = luminance(foreground), b = luminance(background)
      return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
    }
    const backgroundOf = (element: Element | null) => {
      const chain: Element[] = []
      for (let current = element; current; current = current.parentElement) chain.unshift(current)
      let background = [255, 255, 255], unsupported = false
      for (const ancestor of chain) {
        const css = getComputedStyle(ancestor)
        if (css.backgroundImage !== 'none' || Number(css.opacity) !== 1) unsupported = true
        background = blend(color(css.backgroundColor), background)
      }
      return { background, unsupported }
    }
    const contrast: { [key: string]: unknown; ratio: number; required: number; unsupported: boolean }[] = []
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
    let node: Node | null
    while ((node = walker.nextNode())) {
      const element = node.parentElement
      if (!element || !node.textContent?.trim() || !visible(element) || element.closest('svg, script, style, [disabled], [aria-hidden="true"]')) continue
      const range = document.createRange(); range.selectNodeContents(node)
      const rect = range.getBoundingClientRect()
      if (!rect.width || !rect.height) continue
      const style = getComputedStyle(element)
      const { background, unsupported } = backgroundOf(element)
      const foreground = blend(color(style.color), background)
      const ratio = contrastRatio(foreground, background)
      const size = parseFloat(style.fontSize), weight = parseInt(style.fontWeight) || 400
      const required = size >= 24 || (size >= 18.66 && weight >= 700) ? 3 : 4.5
      if (ratio < required || unsupported) contrast.push({
        text: node.textContent.trim().slice(0, 60), tag: element.tagName, class: element.className,
        foreground: style.color, background, fontSize: size, weight,
        ratio: Number(ratio.toFixed(3)), required, unsupported,
      })
    }
    return {
      viewport: innerWidth, client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth,
      theme: document.documentElement.dataset.theme,
      contrastNeedsReview: contrast,
      controls: [...document.querySelectorAll('button,input,select,textarea')].filter(visible).map(element => {
        const rect = element.getBoundingClientRect(), css = getComputedStyle(element)
        const inside = backgroundOf(element), outside = backgroundOf(element.parentElement)
        const border = blend(color(css.borderTopColor), inside.background)
        const hasBorder = parseFloat(css.borderTopWidth) > 0 && !['none', 'hidden'].includes(css.borderTopStyle)
        const formBoundary = element.matches('input:not([type=checkbox]):not([type=radio]):not([type=range]),textarea,select')
        return { tag: element.tagName, class: element.className, testId: element.getAttribute('data-testid'), label: element.getAttribute('aria-label'),
          x: rect.x, y: rect.y, width: rect.width, height: rect.height, disabled: element.matches(':disabled'),
          primary: element.matches('.primary,.btn-primary,[data-testid^="nav-"]'),
          borderColor: css.borderTopColor, borderWidth: css.borderTopWidth, backgroundColor: css.backgroundColor,
          formBoundary, boundaryUnsupported: inside.unsupported || outside.unsupported,
          // Either the outline or a contrasting fill can identify a field against its surroundings.
          boundaryRatio: formBoundary ? Number(Math.max(hasBorder ? contrastRatio(border, outside.background) : 0, contrastRatio(inside.background, outside.background)).toFixed(3)) : null }

      }),
    }
  })
  const path = info.outputPath(label + '-geometry.json')
  await writeFile(path, JSON.stringify(data, null, 2))
  await info.attach(label + '-geometry', { path, contentType: 'application/json' })
  expect(data.scroll, label + ': no global horizontal overflow').toBeLessThanOrEqual(data.client + 1)
  expect(data.contrastNeedsReview.filter(issue => !issue.unsupported), label + ': readable text contrast').toEqual([])
  expect(data.controls.filter(control => !control.disabled && (control.width < 24 || control.height < 24)), label + ': minimum target size').toEqual([])
  expect(data.controls.filter(control => !control.disabled && control.formBoundary && !control.boundaryUnsupported && control.boundaryRatio! < 3), label + ': identifiable field boundaries').toEqual([])
  if (data.viewport <= 390) expect(data.controls.filter(control => !control.disabled && control.primary && (control.width < 44 || control.height < 44)), label + ': mobile primary target size').toEqual([])
  return data
}

for (const theme of ['light', 'dark']) {
  for (const width of [320, 390, 768, 1280]) {
    test(`inventory ${theme} ${width}px: six main views`, async ({ page }, info) => {
      await page.setViewportSize({ width, height: 900 })
      await connect(page, theme, info)
      await page.evaluate(value => { document.documentElement.dataset.theme = value }, theme)
      for (const [key, label, testId] of pages) {
        await page.getByTestId(`nav-${key}`).click()
        await expect(page.getByTestId(testId), label).toBeVisible()
        await capture(page, info, `${theme}-${width}-${key}`)
      }
      await page.getByTestId('nav-configuration').click()
      await page.getByTestId('config-enable-admin').click()
      await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
      await capture(page, info, `${theme}-${width}-configuration-admin`)
      await page.getByTestId('nav-capabilities').click()
      await page.getByTestId('cap-add-provider').click()
      await expect(page.getByTestId('provider-editor')).toBeVisible()
      await capture(page, info, `${theme}-${width}-provider-dialog`)
    })
  }
}
