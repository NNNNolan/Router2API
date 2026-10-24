import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NButton, NInput, NPagination, NSelect } from 'naive-ui'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, type ModelPlazaModel } from '@/services/api'
import ModelPlazaPage from './ModelPlazaPage.vue'

const message = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('naive-ui', async importOriginal => ({ ...await importOriginal<typeof import('naive-ui')>(), useMessage: () => message }))

let view: VueWrapper | undefined
let client: QueryClient | undefined
const models: ModelPlazaModel[] = Array.from({ length: 30 }, (_, index) => ({
  id: `${index < 25 ? 'proxy' : 'other'}/model-${index}`,
  displayName: index === 29 ? 'DeepSeek-R1' : `CustomModel-${index}`,
  platform: index < 25 ? 'proxy' : 'other', platformName: index < 25 ? '代理平台' : '另一平台',
  contextWindow: 128000, inputLimit: 64000, outputLimit: 8000,
  supportsReasoning: true, reasoningLevels: ['high', 'low'], reasoningTokenLimit: null,
}))

afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.clearAllMocks() })

function render() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(ModelPlazaPage, { global: {
    plugins: [[VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true,
    stubs: Object.fromEntries(['NAlert', 'NButton', 'NEmpty', 'NIcon', 'NInput', 'NPagination', 'NSelect', 'NSpin', 'ResponsiveFilterPanel'].map(name => [name, true])),
  } })
  return view
}

describe('模型广场查询与操作', () => {
  it('分页、搜索、平台筛选及每页数量变化保持一致，筛选时回到第一页', async () => {
    vi.spyOn(api, 'modelPlaza').mockResolvedValue({ updatedAt: '2026-10-19T00:00:00Z', source: 'test', models })
    const page = render()
    expect(page.find('.center-spin').exists()).toBe(true)
    await vi.waitFor(() => expect(page.findAll('article')).toHaveLength(20))
    page.getComponent(NPagination).vm.$emit('update:page', 2)
    await flushPromises()
    expect(page.findAll('article')).toHaveLength(10)
    page.getComponent(NInput).vm.$emit('update:value', 'deepseek')
    await flushPromises()
    expect(page.findAll('article')).toHaveLength(1)
    expect(page.getComponent(NPagination).props('page')).toBe(1)
    expect(page.get('article').text()).toContain('DeepSeek-R1')
    page.getComponent(NInput).vm.$emit('update:value', '')
    page.findAllComponents(NSelect)[0]!.vm.$emit('update:value', 'proxy')
    await flushPromises()
    expect(page.text()).toContain('共 25 个模型')
    expect(page.findAll('article').every(card => card.text().includes('代理平台'))).toBe(true)
    page.getComponent(NPagination).vm.$emit('update:page', 2)
    await flushPromises()
    expect(page.findAll('article')).toHaveLength(5)
    page.getComponent(NPagination).vm.$emit('update:pageSize', 40)
    await flushPromises()
    expect(page.findAll('article')).toHaveLength(25)
    expect(page.getComponent(NPagination).props('page')).toBe(1)
    expect(page.getComponent(NPagination).props('pageSizes')).toEqual([20, 40, 80])
    expect(page.getComponent(NPagination).props('showSizePicker')).toBe(true)
  })

  it('刷新替换模型缓存并收敛页码，失败保留已有目录并明确提示', async () => {
    vi.spyOn(api, 'modelPlaza').mockResolvedValue({ updatedAt: '', source: 'test', models })
    const refresh = vi.spyOn(api, 'refreshModelPlaza').mockResolvedValue({ updatedAt: '', source: 'test', models: [models[29]!] })
    const page = render()
    await vi.waitFor(() => expect(page.findAll('article')).toHaveLength(20))
    page.getComponent(NPagination).vm.$emit('update:page', 2)
    page.getComponent(NButton).vm.$emit('click')
    await vi.waitFor(() => expect(page.findAll('article')).toHaveLength(1))
    expect(page.getComponent(NPagination).props('page')).toBe(1)
    expect(message.success).toHaveBeenCalled()
    refresh.mockRejectedValue(new Error('刷新失败'))
    page.getComponent(NButton).vm.$emit('click')
    await vi.waitFor(() => expect(message.error).toHaveBeenCalledWith('刷新失败'))
    expect(page.get('article').text()).toContain('DeepSeek-R1')
  })

  it('目录加载失败显示重试入口，重试后空目录显示空态', async () => {
    const load = vi.spyOn(api, 'modelPlaza').mockRejectedValue(new Error('目录失败'))
    const page = render()
    await vi.waitFor(() => expect(page.text()).toContain('目录失败'), { timeout: 4000 })
    load.mockResolvedValue({ updatedAt: '', source: 'test', models: [] })
    page.findAllComponents(NButton).at(-1)!.vm.$emit('click')
    await vi.waitFor(() => expect(page.find('.model-plaza-empty').exists()).toBe(true))
    expect(page.find('article').exists()).toBe(false)
  })
})
