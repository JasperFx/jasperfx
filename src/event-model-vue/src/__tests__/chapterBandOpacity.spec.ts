/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest'
import source from '../EventModelView.vue?raw'

/**
 * A chapter band's name was painted at 10%, because the band faded its fill with `opacity`, and opacity applies to
 * the whole subtree: no child can be drawn at full strength inside it. happy-dom applies no stylesheet, so this reads
 * the rules themselves: a rule that targets the band element must not set `opacity`.
 */
describe('chapter band', () => {
  it('fades its fill, never its subtree, so its name stays readable', () => {
    const style = source.slice(source.indexOf('<style'))
    const rules = [...style.matchAll(/([^{}]+)\{([^}]*)\}/g)]
      .map(([, selector, body]) => ({ selector: selector.trim(), body }))
      .filter((r) => r.selector.split(',').some((s: string) => /\.em-chapter-band(\[[^\]]*\]|:[a-z-]+)*\s*$/.test(s.trim())))

    expect(rules.length).toBeGreaterThan(0)
    for (const rule of rules) expect(rule.body, rule.selector).not.toMatch(/(^|[;\s])opacity\s*:/)
  })
})
