import { describe, expect, it } from 'vitest'
import { activityGrid } from './activityHeatmap'

describe('按自然日绘制热力图', () => {
  it('跨年闰年，补齐周一至周日但不把窗口外格子计入活跃', () => {
    const grid = activityGrid('2023-03-02', '2024-02-29', [{ day: '2024-02-29', requests: 10 }, { day: '2023-03-01', requests: 1000 }])
    const cells = grid.weeks.flat()
    expect(cells.filter(cell => cell.inRange)).toHaveLength(365)
    expect(new Set(cells.map(cell => cell.day)).size).toBe(cells.length)
    expect(cells.find(cell => cell.day === '2024-02-29')?.level).toBe(4)
    expect(cells.find(cell => cell.day === '2023-03-01')).toMatchObject({ inRange: false, requests: 0, level: 0 })
    expect(grid.weeks.every(week => week.length === 7)).toBe(true)
    expect(grid.months[0]).toContain('2023')
  })
  it('空数据真实为零，强度按窗口峰值分五档', () => {
    expect(activityGrid('2026-10-01', '2026-10-07', []).weeks.flat().every(cell => cell.level === 0)).toBe(true)
    const cells = activityGrid('2026-10-01', '2026-10-07', [1, 2, 3, 4].map(value => ({ day: `2026-10-0${value}`, requests: value }))).weeks.flat()
    expect(cells.filter(cell => cell.requests).map(cell => cell.level)).toEqual([1, 2, 3, 4])
  })
})
