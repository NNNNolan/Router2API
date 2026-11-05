import { mount } from '@vue/test-utils'
import { expect, it } from 'vitest'
import ActivityHeatmap from './ActivityHeatmap.vue'

it('键盘聚焦和触摸点击展示每日计数，说明日志保留边界', async () => {
  const view = mount(ActivityHeatmap, { props: { report: {
    fromDay: '2026-10-01', toDay: '2026-10-07', timezone: 'Asia/Shanghai', retentionDays: 30,
    earliestLogUtc: null, days: [{ day: '2026-10-03', requests: 8 }], requests: 8, activeDays: 1,
  } } })
  const cell = view.get('[data-day="2026-10-03"]')
  await cell.trigger('focusin')
  expect(view.get('[aria-live]').text()).toBe('2026-10-03 · 8 次请求')
  await view.get('[data-day="2026-10-01"]').trigger('click')
  expect(view.get('[aria-live]').text()).toBe('2026-10-01 · 0 次请求')
  expect(view.text()).toContain('基于已保留日志')
  expect(view.text()).toContain('当前保留 30 天')
  expect(view.findAll('button')).toHaveLength(7)
  view.unmount()
})
