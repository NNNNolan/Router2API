import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { NTabs } from 'naive-ui'
import { afterEach, expect, it, vi } from 'vitest'
import { api, type ObservedRequest, type TraceStage } from '@/services/api'
import PrivacyPage from './PrivacyPage.vue'
import TracesPage from './TracesPage.vue'
vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => ({ success: vi.fn(), error: vi.fn() }),
}))

let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks() })
const page = { items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 }
const stubs = Object.fromEntries(['NAlert', 'NButton', 'NCard', 'NDataTable', 'NEmpty', 'NFormItem', 'NInput', 'NPagination', 'NSelect', 'NSpin', 'NSwitch', 'NTabPane', 'NTabs', 'NTag'].map(name => [name, true]))

it('护栏关闭状态仍能编辑规则和使用测试，语音边界始终可见', async () => {
  vi.spyOn(api, 'privacySettings').mockResolvedValue({ enabled: false, dryRun: true, defaultAction: 'warn', categoryActions: {}, customRules: [] })
  vi.spyOn(api, 'privacySummary').mockResolvedValue({ inspected: 0, matched: 0, blocked: 0, dryRun: 0, redactions: 0, categories: [] })
  vi.spyOn(api, 'privacyEvents').mockResolvedValue(page)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(PrivacyPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true, stubs } })
  await flushPromises()
  expect(view.text()).toContain('语音合成不受保护')
  expect(view.text()).toContain('仅检测模式')
  expect(view.html()).toContain('暂无命中')
  view.getComponent(NTabs).vm.$emit('update:value', 'rules')
  await flushPromises()
  expect(view.text()).toContain('保存设置')
  view.getComponent(NTabs).vm.$emit('update:value', 'tester')
  await flushPromises()
  expect(view.text()).toContain('检测文本')
})

it('旧请求明确显示无追踪详情，不构造伪时间线', async () => {
  vi.spyOn(api, 'plugins').mockResolvedValue([])
  vi.spyOn(api, 'traceTopology').mockResolvedValue({ clients: [], links: [] })
  vi.spyOn(api, 'traceRequests').mockResolvedValue(page)
  vi.spyOn(api, 'traceRequest').mockResolvedValue({ id: 'old', traceId: 'old-trace', endpoint: null, platform: null, model: 'old-model', success: true, cancelled: false, statusCode: 200, durationMs: 1, createdAt: '2026-10-01T00:00:00Z', privacy: null, trace: null })
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/traces/requests/:id', component: TracesPage }] })
  await router.push('/traces/requests/old')
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(TracesPage, { global: { plugins: [router, [VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true, stubs } })
  await flushPromises()
  expect(view.find('.trace-waterfall').exists()).toBe(false)
  expect(view.text()).toContain('不追踪插件内部上游')
  expect(view.html()).toContain('无追踪详情')
  expect(api.traceRequest).toHaveBeenCalledWith('old')
})

it('拓扑按插件合并入口，尝试平均耗时按次数加权，点击保留真实插件筛选', async () => {
  vi.spyOn(api, 'plugins').mockResolvedValue([])
  vi.spyOn(api, 'traceTopology').mockResolvedValue({
    clients: [
      { endpoint: '/v1/chat/completions', requests: 3, failures: 0, averageLatencyMs: 400 },
      { endpoint: '/v1/responses', requests: 1, failures: 1, averageLatencyMs: 500 },
    ],
    links: [
      { endpoint: '/v1/chat/completions', pluginKey: 'demo', requests: 3, attempts: 3, failures: 0, averageLatencyMs: 100 },
      { endpoint: '/v1/responses', pluginKey: 'demo', requests: 1, attempts: 1, failures: 1, averageLatencyMs: 300 },
    ],
  })
  vi.spyOn(api, 'traceRequests').mockResolvedValue(page)
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/traces', component: TracesPage }] })
  await router.push('/traces')
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(TracesPage, { global: { plugins: [router, [VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true, stubs } })
  await flushPromises()
  const plugin = view.get('.trace-plugin')
  expect(view.findAll('.trace-plugin')).toHaveLength(1)
  expect(plugin.text()).toContain('150 ms')
  expect(plugin.text()).toContain('4 尝试')
  expect(plugin.text()).toContain('Chat Completions')
  expect(plugin.text()).toContain('Responses')
  expect(view.get('.trace-host').text()).toContain('4次请求')
  expect(view.text()).toContain('失败尝试不等于请求最终失败')
  await plugin.trigger('click')
  await flushPromises()
  expect(api.traceRequests).toHaveBeenLastCalledWith(expect.objectContaining({ pluginKey: 'demo', page: 1 }))
  expect(view.get('.trace-plugin').attributes('aria-pressed')).toBe('true')
})

it('瀑布图使用实际时间和父子层级，同时显示失败与取消，不重复阶段表格', async () => {
  vi.spyOn(api, 'plugins').mockResolvedValue([])
  vi.spyOn(api, 'traceTopology').mockResolvedValue({ clients: [], links: [] })
  vi.spyOn(api, 'traceRequests').mockResolvedValue(page)
  const stage: TraceStage = { id: 'target', parentId: 'root', name: 'mapping.target', startMs: 100, durationMs: 500, outcome: 'completed', statusCode: 200, pluginKey: 'demo', platform: 'demo', model: 'model' }
  const request: ObservedRequest = { id: 'request', traceId: 'trace', endpoint: '/v1/responses', platform: 'demo', model: 'alias', success: false, cancelled: true, statusCode: 200, durationMs: 1000, createdAt: '2026-11-05T00:00:00Z', privacy: null,
    trace: { endpoint: '/v1/responses', startedAt: '2026-11-05T00:00:00Z', durationMs: 1000, truncated: false, stages: [
      { ...stage, id: 'attempt', parentId: 'target', name: 'plugin.attempt', startMs: 200, durationMs: 100, outcome: 'failed', statusCode: 503 },
      stage,
    ] },
  }
  vi.spyOn(api, 'traceRequest').mockResolvedValue(request)
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/traces/requests/:id', component: TracesPage }] })
  await router.push('/traces/requests/request')
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(TracesPage, { global: { plugins: [router, [VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true, stubs } })
  await flushPromises()
  const rows = view.findAll('.trace-stage-row[data-depth]')
  expect(rows.map(row => row.attributes('data-depth'))).toEqual(['0', '1'])
  expect(rows[1]!.get('i').attributes('style')).toContain('left: 20%')
  expect(rows[1]!.get('i').attributes('style')).toContain('width: 10%')
  expect(rows[1]!.text()).toContain('失败 · 503')
  expect(rows[1]!.get('[role="img"]').attributes('aria-label')).toContain('开始 200 ms，耗时 100 ms')
  expect(view.get('.trace-axis').text()).toContain('1,000 ms')
  expect(view.get('.trace-detail-summary').text()).toContain('取消 · HTTP 200')
  expect(view.findComponent({ name: 'NDataTable' }).exists()).toBe(false)
})
