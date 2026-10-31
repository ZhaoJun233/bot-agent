import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import favicon from '../../src/favicon.svg?raw'
import BrandMark from '../../src/shared/ui/BrandMark.vue'

describe('minimal conversation branding', () => {
  it.each([16, 26, 32])('keeps a square decorative mark at %i pixels', size => {
    const wrapper = mount(BrandMark, { props: { size } })
    expect(wrapper.attributes('width')).toBe(String(size))
    expect(wrapper.attributes('height')).toBe(String(size))
    expect(wrapper.attributes('viewBox')).toBe('0 0 32 32')
    expect(wrapper.attributes('aria-hidden')).toBe('true')
    expect(wrapper.attributes('focusable')).toBe('false')
    expect(wrapper.findAll('ellipse')).toHaveLength(0)
    expect(wrapper.findAll('path')).toHaveLength(1)
    expect(wrapper.findAll('circle')).toHaveLength(3)
    wrapper.unmount()
  })
  it('uses the same conversation geometry in the app and favicon', () => {
    const wrapper = mount(BrandMark)
    const document = new DOMParser().parseFromString(favicon, 'image/svg+xml')
    const shapes = Array.from(document.documentElement.children).slice(1)
    expect(shapes).toHaveLength(wrapper.element.children.length)
    for (let index = 0; index < shapes.length; index++) {
      const actual = wrapper.element.children[index]!
      expect(actual.tagName.toLowerCase()).toBe(shapes[index]!.tagName.toLowerCase())
      for (const attribute of Array.from(shapes[index]!.attributes)) expect(actual.getAttribute(attribute.name)).toBe(attribute.value)
    }
    wrapper.unmount()
  })
})
