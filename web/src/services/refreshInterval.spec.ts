// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { formatRefreshInterval, isValidRefreshInterval } from './refreshInterval'

describe('代理订阅刷新周期', () => {
  it.each([
    ['1S', 1], [' 30s ', 30], ['90S', 90], ['1M', 60],
    ['30M', 1800], ['2H', 7200], ['2147483647M', 128849018820],
  ])('接受 %s 且格式化后仍可保存', (input, seconds) => {
    expect(isValidRefreshInterval(input)).toBe(true)
    expect(isValidRefreshInterval(formatRefreshInterval(seconds))).toBe(true)
  })

  it.each(['', '0S', '-1S', '0.5M', '01S', '1 S', '1D', '1e2S', '2147483648M', '128849018821S', '999999999999999999999H'])(
    '拒绝非法或溢出的周期 %s', input => expect(isValidRefreshInterval(input)).toBe(false),
  )

  it.each([[30, '30S'], [90, '90S'], [60, '1M'], [1800, '30M'], [7200, '2H']])(
    '精确显示 %i 秒为 %s', (seconds, text) => expect(formatRefreshInterval(seconds)).toBe(text),
  )
})
