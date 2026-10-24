import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NAlert, NButton, NDataTable, NInput, NSelect } from 'naive-ui'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, type AnalyticsReport } from '@/services/api'
import AnalyticsPage from './AnalyticsPage.vue'

vi.mock('vue-echarts', () => ({ default: { name: 'VChart', props: ['option'], template: '<div class="test-chart" />' } }))

const report: AnalyticsReport = {
  fromUtc: '2026-10-19T00:00:00Z', toUtc: '2026-10-19T01:00:00Z', requests: 2, successes: 1, failures: 1,
  inputTokens: 0, outputTokens: 7, tokens: 7, averageLatencyMs: 100, p95LatencyMs: 150,
  estimatedRequests: 1, partialRequests: 1, unknownUsageRequests: 1, byPlatform: { proxy: 2 },
  buckets: [{ startUtc: '2026-10-19T00:00:00Z', requests: 2, successes: 1, failures: 1, tokens: 7, inputTokens: 0, outputTokens: 7, averageLatencyMs: 100, p95LatencyMs: 150 }],
  platforms: [{ name: 'proxy', requests: 2, inputTokens: 0, outputTokens: 7, tokens: 7, estimatedRequests: 1 }],
  models: [{ name: 'proxy/DeepSeek-R1', requests: 2, inputTokens: 0, outputTokens: 7, tokens: 7, estimatedRequests: 1 }],
}
let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

function render() {
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() }))
  vi.spyOn(api, 'platforms').mockResolvedValue([])
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(AnalyticsPage, { global: {
    plugins: [[VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true,
    stubs: Object.fromEntries(['NAlert', 'NButton', 'NCard', 'NDataTable', 'NEmpty', 'NInput', 'NSelect', 'NSpin'].map(name => [name, true])),
  } })
  return view
}

describe('用量分析', () => {
  it('总数、趋势、平台与模型汇总使用同一份接口数据，保留合法零值', async () => {
    vi.spyOn(api, 'analyticsOverview').mockResolvedValue({ range: report, live: { activeConnections: 3 } })
    const page = render()
    await vi.waitFor(() => expect(page.findAll('.metric-value')).toHaveLength(8))
    expect(page.findAll('.metric-value').map(value => value.text())).toEqual(['2', '50.0%', '0', '7', '7', '100 ms', '150 ms', '3'])
    expect(page.text()).toContain('1 次包含宿主估算')
    expect(page.text()).toContain('1 次记录不完整')
    expect(page.text()).toContain('1 次来源未知')
    const charts = page.findAllComponents({ name: 'VChart' })
    expect(charts[1]!.props('option').series.map((series: { data: number[] }) => series.data)).toEqual([[0], [7]])
    expect(page.findAllComponents(NDataTable).map(table => table.props('data'))).toEqual([report.platforms, report.models])
    page.getComponent(NInput).vm.$emit('update:value', ' proxy/DeepSeek-R1 ')
    page.findAllComponents(NSelect).at(-1)!.vm.$emit('update:value', 'proxy')
    await flushPromises()
    expect(api.analyticsOverview).toHaveBeenLastCalledWith(expect.objectContaining({ platform: 'proxy', model: 'proxy/DeepSeek-R1' }))
  })

  it('加载失败明确显示错误，刷新可恢复，空目录不绘制虚假趋势', async () => {
    const load = vi.spyOn(api, 'analyticsOverview').mockRejectedValue(new Error('unavailable'))
    const page = render()
    await vi.waitFor(() => expect(page.findComponent(NAlert).exists()).toBe(true))
    expect(page.find('.test-chart').exists()).toBe(false)
    expect(page.find('.metric-value').exists()).toBe(false)
    load.mockResolvedValue({ range: { ...report, requests: 0, buckets: [] }, live: { activeConnections: 0 } })
    page.getComponent(NButton).vm.$emit('click')
    await vi.waitFor(() => expect(page.findAll('.metric-value')).toHaveLength(8))
    expect(page.find('.test-chart').exists()).toBe(false)
    expect(page.findAll('.metric-value')[1]!.text()).toBe('—')
  })
})
