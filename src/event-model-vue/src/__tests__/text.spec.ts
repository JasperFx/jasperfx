import { describe, expect, it } from 'vitest'
import { estimateTextWidth, requiredContentWidth, segmentLabel } from '../text'
import { parseRoute } from '../icons'
import { LABEL_FONT_SIZE, LABEL_TARGET_LINES } from '../layout'

/**
 * bobcat#180. The estimate is allowed to be a little wrong — it only chooses a column width, and
 * wrapping, the line clamp and the tooltip absorb the error. What it must never do is depend on
 * the DOM, which is why every assertion here is on a pure function of the label string.
 */
describe('segmentLabel', () => {
  it('breaks a PascalCase name at its humps', () => {
    expect(segmentLabel('DepositMoneyIntoAccount')).toEqual(['Deposit', 'Money', 'Into', 'Account'])
  })

  it('keeps a delimiter with the segment it ends, so a route never orphans its slash', () => {
    expect(segmentLabel('POST /accounts/{id}/deposit')).toEqual([
      'POST ',
      '/',
      'accounts/',
      '{id}/',
      'deposit'
    ])
  })

  it('breaks a namespaced type name after each dot', () => {
    expect(segmentLabel('Bank.Accounts.WithdrawFunds')).toEqual([
      'Bank.',
      'Accounts.',
      'Withdraw',
      'Funds'
    ])
  })

  it('leaves an acronym run whole rather than breaking every capital', () => {
    // ...because the hump rule only fires on lower-then-upper. HTTPListener is two segments.
    expect(segmentLabel('HTTPListener')).toEqual(['HTTPListener'])
  })

  it('returns the label itself for an empty or single-segment name', () => {
    expect(segmentLabel('')).toEqual([''])
    expect(segmentLabel('Balance')).toEqual(['Balance'])
  })
})

describe('estimateTextWidth', () => {
  it('charges capitals more than narrow lowercase, which is the error that matters', () => {
    expect(estimateTextWidth('WWWWWWWWWW', 13)).toBeGreaterThan(
      2 * estimateTextWidth('llllllllll', 13)
    )
  })

  it('scales with the font size', () => {
    expect(estimateTextWidth('Account', 26)).toBeCloseTo(2 * estimateTextWidth('Account', 13))
  })

  it('is zero for an empty string', () => {
    expect(estimateTextWidth('', 13)).toBe(0)
  })
})

describe('requiredContentWidth', () => {
  it('never asks for less than its longest unbreakable segment', () => {
    const label = 'ReconciliationBatchCompleted'
    const longest = Math.max(
      ...segmentLabel(label).map((s) => estimateTextWidth(s, LABEL_FONT_SIZE))
    )
    expect(requiredContentWidth(label, LABEL_FONT_SIZE, LABEL_TARGET_LINES)).toBeGreaterThanOrEqual(
      longest
    )
  })

  it('asks for the whole label on one line when only one line is allowed', () => {
    const label = 'WithdrawFunds'
    expect(requiredContentWidth(label, LABEL_FONT_SIZE, 1)).toBe(
      estimateTextWidth(label, LABEL_FONT_SIZE)
    )
  })

  it('asks for less width as it is allowed more lines', () => {
    const label = 'PUT /api/organizations/{orgId}/subscriptions/{id}/cancel'
    const two = requiredContentWidth(label, LABEL_FONT_SIZE, 2)
    const three = requiredContentWidth(label, LABEL_FONT_SIZE, 3)
    expect(three).toBeLessThan(two)
    expect(two).toBeLessThan(requiredContentWidth(label, LABEL_FONT_SIZE, 1))
  })
})

describe('parseRoute', () => {
  it('splits a route label into its verb and path', () => {
    expect(parseRoute('POST /api/accounts/{id}/withdrawals')).toEqual({
      method: 'POST',
      path: '/api/accounts/{id}/withdrawals'
    })
  })

  it('is not fooled by prose that happens to start with a word', () => {
    // "An agent claims ready work" must stay a sentence, not become a badge and a fragment.
    expect(parseRoute('An agent claims ready work')).toBeNull()
    expect(parseRoute('WithdrawFunds')).toBeNull()
    expect(parseRoute('Teller screen')).toBeNull()
  })

  it('accepts a lower-cased verb and normalizes it', () => {
    expect(parseRoute('get /api/plans')).toEqual({ method: 'GET', path: '/api/plans' })
  })
})
