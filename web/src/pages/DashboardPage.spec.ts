import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, type AnalyticsReport, type Metrics } from '@/services/api'
import DashboardPage from './DashboardPage.vue'

vi.mock('vue-echarts', () => ({ default: { name: 'VChart', props: ['option'], template: '<div class="test-chart" />' } }))

let view: VueWrapper | undefined
let client: QueryClient | undefined
let motionChanged: (() => void) | undefined
let reduced = false
const removeListener = vi.fn()
const metrics: Metrics = { totalRequests: 100, successfulRequests: 90, failedRequests: 10, requestsToday: 20, requestsPerMinute: 3, activeRequests: 2, averageLatencyMs: 125, platforms: [] }
const report: AnalyticsReport = {
  fromUtc: '2026-10-18T00:00:00Z', toUtc: '2026-10-18T02:00:00Z', requests: 20, successes: 19, failures: 1, tokens: 0, averageLatencyMs: 125, p95LatencyMs: 200, byPlatform: {},
  buckets: [
    { startUtc: '2026-10-18T00:00:00Z', requests: 6, successes: 5, failures: 1, tokens: 0, averageLatencyMs: 125, p95LatencyMs: 200 },
    { startUtc: '2026-10-18T01:00:00Z', requests: 14, successes: 14, failures: 0, tokens: 0, averageLatencyMs: 125, p95LatencyMs: 200 },
  ],
}

afterEach(() => {
  view?.unmount()
  client?.clear()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

async function render(options: { empty?: boolean; fail?: boolean } = {}) {
  reduced = false
  motionChanged = undefined
  removeListener.mockClear()
  vi.stubGlobal('matchMedia', () => ({
    get matches() { return reduced },
    addEventListener: (_: string, listener: () => void) => { motionChanged = listener },
    removeEventListener: removeListener,
  }))
  const getMetrics = vi.spyOn(api, 'metrics')
  const getTrend = vi.spyOn(api, 'analytics')
  if (options.fail) {
    getMetrics.mockRejectedValue(new Error('Unavailable'))
    getTrend.mockRejectedValue(new Error('Unavailable'))
  } else {
    getMetrics.mockResolvedValue(options.empty ? { ...metrics, successfulRequests: 0, failedRequests: 0 } : metrics)
    getTrend.mockResolvedValue(options.empty ? { ...report, buckets: [], requests: 0 } : report)
  }
  vi.spyOn(api, 'platforms').mockResolvedValue([])
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(DashboardPage, {
    global: {
      plugins: [[VueQueryPlugin, { queryClient: client }]],
      renderStubDefaultSlot: true,
      stubs: Object.fromEntries(['NAlert', 'NButton', 'NCard', 'NDataTable', 'NEmpty', 'NIcon', 'NSpin', 'NTag', 'RouterLink'].map(name => [name, true])),
    },
  })
  await flushPromises()
  await vi.waitFor(() => expect(getTrend).toHaveBeenCalled())
  await flushPromises()
  return view
}

describe('总览数据与动效', () => {
  it('趋势只使用分析接口的请求桶，不把 RPM 或累计请求作为历史点', async () => {
    const page = await render()
    expect(api.analytics).toHaveBeenCalledWith(expect.objectContaining({ bucket: 'hour', fromUtc: expect.any(Date), toUtc: expect.any(Date) }))
    const option = page.getComponent({ name: 'VChart' }).props('option')
    expect(option.series[0].data).toEqual([6, 14])
    expect(option.series[1].data).toEqual([1, 0])
    expect(page.findAll('.metric-value').map(item => item.text())).toEqual(['20次', '90.0%', '125ms', '2个'])
  })

  it('指标和趋势失败时显示错误，不展示伪造的零值或曲线', async () => {
    const page = await render({ fail: true })
    await vi.waitFor(() => expect(page.text()).toContain('指标暂时不可用'))
    expect(page.text()).toContain('请求趋势加载失败')
    expect(page.findAll('.metric-card')).toHaveLength(0)
    expect(page.find('.test-chart').exists()).toBe(false)
  })

  it('没有已完成请求时成功率显示空值，无日志时不绘制趋势', async () => {
    const page = await render({ empty: true })
    expect(page.findAll('.metric-value')[1].text()).toBe('—')
    expect(page.text()).toContain('暂无已完成的请求')
    expect(page.find('.test-chart').exists()).toBe(false)
  })

  it('系统减少动态效果的偏好变化会关闭 Canvas 动画，并在卸载时移除监听', async () => {
    const page = await render()
    expect(page.getComponent({ name: 'VChart' }).props('option').animation).toBe(true)
    reduced = true
    motionChanged!()
    await flushPromises()
    expect(page.getComponent({ name: 'VChart' }).props('option').animation).toBe(false)
    page.unmount()
    view = undefined
    expect(removeListener).toHaveBeenCalledWith('change', motionChanged)
  })
})
