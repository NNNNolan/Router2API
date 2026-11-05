import type { ActivityHeatmapDay } from './api'

export interface HeatmapCell { day: string; requests: number; level: number; inRange: boolean }
// 日历键是日期而非时刻；用 UTC 计算网格，避免浏览器 DST 造成重复/遗漏格子。
export function activityGrid(fromDay: string, toDay: string, days: ActivityHeatmapDay[]) {
  const first = new Date(`${fromDay}T00:00:00Z`)
  const last = new Date(`${toDay}T00:00:00Z`)
  const start = new Date(first)
  start.setUTCDate(start.getUTCDate() - (start.getUTCDay() + 6) % 7)
  const end = new Date(last)
  end.setUTCDate(end.getUTCDate() + 6 - (end.getUTCDay() + 6) % 7)
  const counts = new Map(days.filter(day => day.day >= fromDay && day.day <= toDay).map(day => [day.day, day.requests]))
  const peak = Math.max(0, ...counts.values())
  const weeks: HeatmapCell[][] = []
  for (const date = new Date(start); date <= end; date.setUTCDate(date.getUTCDate() + 1)) {
    if ((date.getUTCDay() + 6) % 7 === 0) weeks.push([])
    const day = date.toISOString().slice(0, 10)
    const inRange = day >= fromDay && day <= toDay
    const requests = inRange ? counts.get(day) ?? 0 : 0
    weeks.at(-1)!.push({ day, requests, level: requests > 0 ? Math.min(4, Math.ceil(requests / peak * 4)) : 0, inRange })
  }
  const months = weeks.map((week, index) => {
    const firstOfMonth = week.find(cell => cell.day.endsWith('-01') && cell.inRange)
    if (firstOfMonth) return `${Number(firstOfMonth.day.slice(5, 7))}月`
    return index === 0 ? `${fromDay.slice(0, 4)} · ${Number(fromDay.slice(5, 7))}月` : ''
  })
  return { weeks, months }
}
