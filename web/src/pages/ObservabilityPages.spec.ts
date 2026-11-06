import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { NButton, NInput, NTabs } from 'naive-ui'
import { afterEach, expect, it, vi } from 'vitest'
import { api, type ObservedRequest, type PrivacySettings, type TraceStage } from '@/services/api'
import PrivacyPage from './PrivacyPage.vue'
import TracesPage from './TracesPage.vue'
vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => ({ success: vi.fn(), error: vi.fn() }),
}))

let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.unstubAllGlobals() })
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

it('HTTP 环境缺少 randomUUID 时仍能连续添加、编辑并保存唯一规则', async () => {
  // 普通 HTTP 地址不提供 randomUUID，但仍提供 getRandomValues。
  vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) })
  const initial: PrivacySettings = { enabled: false, dryRun: true, defaultAction: 'warn', categoryActions: {}, customRules: [
    { id: 'existing-rule', name: '已有规则', pattern: 'EXISTING', enabled: true, action: null },
  ] }
  const read = vi.spyOn(api, 'privacySettings').mockResolvedValue(initial)
  const save = vi.spyOn(api, 'savePrivacySettings').mockImplementation(async value => {
    read.mockResolvedValue(value)
    return value
  })
  vi.spyOn(api, 'privacySummary').mockResolvedValue({ inspected: 0, matched: 0, blocked: 0, dryRun: 0, redactions: 0, categories: [] })
  vi.spyOn(api, 'privacyEvents').mockResolvedValue(page)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(PrivacyPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true, stubs } })
  await flushPromises()
  view.getComponent(NTabs).vm.$emit('update:value', 'rules')
  await flushPromises()
  const add = view.findAllComponents(NButton).find(button => button.text() === '添加规则')!
  for (let index = 0; index < 2; index++) {
    add.vm.$emit('click')
    await flushPromises()
  }
  const rows = view.findAll('.privacy-custom-rule')
  expect(rows).toHaveLength(3)
  for (let index = 1; index < rows.length; index++) {
    const inputs = rows[index]!.findAllComponents(NInput)
    inputs[0]!.vm.$emit('update:value', `手机规则 ${index}`)
    inputs[1]!.vm.$emit('update:value', `CONFIDENTIAL_${index}`)
  }
  view.findAllComponents(NButton).find(button => button.text() === '保存设置')!.vm.$emit('click')
  await flushPromises()
  expect(save).toHaveBeenCalledTimes(1)
  const saved = save.mock.calls[0]![0].customRules
  expect(saved[0]).toEqual(initial.customRules[0])
  expect(saved.slice(1)).toEqual([1, 2].map(index => ({
    id: expect.stringMatching(/^[A-Za-z0-9_-]{1,64}$/), name: `手机规则 ${index}`, pattern: `CONFIDENTIAL_${index}`, enabled: true, action: null,
  })))
  expect(new Set(saved.map(rule => rule.id)).size).toBe(3)
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
