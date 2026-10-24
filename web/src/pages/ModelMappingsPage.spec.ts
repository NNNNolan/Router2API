import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NButton, NForm, NInput, NInputNumber, NPopconfirm, NSelect } from 'naive-ui'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, type ModelMapping } from '@/services/api'
import ModelMappingsPage from './ModelMappingsPage.vue'

const message = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('naive-ui', async importOriginal => ({ ...await importOriginal<typeof import('naive-ui')>(), useMessage: () => message }))

let view: VueWrapper | undefined
let client: QueryClient | undefined
const mapping: ModelMapping = {
  id: 'mapping-id', name: 'ds4', enabled: true,
  targets: [
    { pluginKey: 'plugin-a', platform: 'a', model: 'real-a', enabled: true, retryCount: 1 },
    { pluginKey: 'plugin-b', platform: 'b', model: 'real-b', enabled: true, retryCount: null },
  ],
}

afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.clearAllMocks() })

async function render(initial: ModelMapping[] = []) {
  vi.spyOn(api, 'modelMappings').mockResolvedValue(initial)
  vi.spyOn(api, 'platforms').mockResolvedValue([
    { name: 'a', pluginKey: 'plugin-a', displayName: '平台 A', enabled: true, probeEndpoint: null },
    { name: 'b', pluginKey: 'plugin-b', displayName: '平台 B', enabled: true, probeEndpoint: null },
  ])
  vi.spyOn(api, 'modelPlaza').mockResolvedValue({ updatedAt: '', source: 'test', models: [] })
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(ModelMappingsPage, { global: {
    plugins: [[VueQueryPlugin, { queryClient: client }]], renderStubDefaultSlot: true,
    stubs: Object.fromEntries(['NAlert', 'NButton', 'NCard', 'NEmpty', 'NForm', 'NFormItem', 'NInput', 'NInputNumber', 'NModal', 'Modal', 'NPopconfirm', 'NSelect', 'NSpin', 'NSwitch', 'NTag'].map(name => [name, true])),
  } })
  await flushPromises()
  return view
}

async function click(text: string) {
  const button = view!.findAllComponents(NButton).find(button => button.text() === text)!
  expect(button).toBeDefined()
  button.vm.$emit('click')
  await flushPromises()
}

describe('模型映射管理', () => {
  it.each([
    ['cn/deepseek-v4-flash', 'deepseek-v4-flash'],
    ['vendor/cn/deepseek-v4-flash', 'deepseek-v4-flash'],
    ['deepseek-v4-flash', 'deepseek-v4-flash'],
  ])('名称为空时从首次选择的 %s 填入 %s，保留实际模型 ID', async (model, expectedName) => {
    const save = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(mapping)
    const page = await render()
    await click('新建映射')
    const name = page.findAllComponents(NInput).find(input => input.attributes('aria-label') === '对外模型名')!
    name.vm.$emit('update:value', '  ')
    page.findAllComponents(NSelect)[0]!.vm.$emit('update:value', 'a')
    await flushPromises()
    page.findAllComponents(NSelect)[1]!.vm.$emit('update:value', model)
    await flushPromises()
    expect(name.props('value')).toBe(expectedName)
    await click('添加目标')
    page.findAllComponents(NSelect)[2]!.vm.$emit('update:value', 'b')
    await flushPromises()
    page.findAllComponents(NSelect)[3]!.vm.$emit('update:value', 'other-model')
    await flushPromises()
    expect(name.props('value')).toBe(expectedName)
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
      name: expectedName, targets: [
        expect.objectContaining({ model }), expect.objectContaining({ model: 'other-model' }),
      ],
    })))
  })

  it('创建时按包含首次的总尝试次数保存，并保留目标顺序和继承设置', async () => {
    const save = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(mapping)
    const page = await render()
    await click('新建映射')
    page.findAllComponents(NInput).find(input => input.attributes('aria-label') === '对外模型名')!.vm.$emit('update:value', ' ds4 ')
    page.findAllComponents(NSelect)[0]!.vm.$emit('update:value', 'a')
    await flushPromises()
    page.findAllComponents(NSelect)[1]!.vm.$emit('update:value', 'custom-a')
    expect(page.getComponent(NInputNumber).props()).toMatchObject({ min: 1, max: 35 })
    page.getComponent(NInputNumber).vm.$emit('update:value', 3)
    await click('添加目标')
    expect(page.text()).toContain('此目标最多调用 3 次，包含首次请求。')
    page.findAllComponents(NSelect)[2]!.vm.$emit('update:value', 'b')
    await flushPromises()
    page.findAllComponents(NSelect)[3]!.vm.$emit('update:value', 'custom-b')
    page.findAllComponents(NButton).find(button => button.attributes('aria-label') === '上移目标 2')!.vm.$emit('click')
    await flushPromises()
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(save).toHaveBeenCalled())
    expect(save).toHaveBeenCalledWith({ id: '', name: 'ds4', enabled: true, targets: [
      { pluginKey: 'plugin-b', platform: 'b', model: 'custom-b', enabled: true, retryCount: null },
      { pluginKey: 'plugin-a', platform: 'a', model: 'custom-a', enabled: true, retryCount: 3 },
    ] })
    await vi.waitFor(() => expect(message.success).toHaveBeenCalledWith('模型映射已保存'))
    expect(page.find('.mapping-editor').exists()).toBe(false)
  })

  it('失败保留编辑内容，取消不改变已保存顺序', async () => {
    const save = vi.spyOn(api, 'saveModelMapping').mockRejectedValue(new Error('对外模型名已存在。'))
    const page = await render([mapping])
    await click('编辑')
    page.findAllComponents(NButton).find(button => button.attributes('aria-label') === '上移目标 2')!.vm.$emit('click')
    await flushPromises()
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(page.text()).toContain('对外模型名已存在。'))
    expect(save.mock.calls[0]![0].targets.map(target => target.platform)).toEqual(['b', 'a'])
    expect(page.find('.mapping-editor').exists()).toBe(true)
    await click('取消')
    expect(page.findAll('.mapping-route-model code').map(model => model.text())).toEqual(['real-a', 'real-b'])
  })

  it('在发送前校验名称和目标，明确显示空态', async () => {
    const save = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(mapping)
    const page = await render()
    expect(page.text()).toContain('暂无模型映射')
    await click('新建映射')
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await flushPromises()
    expect(page.text()).toContain('对外模型名须为')
    page.findAllComponents(NInput).find(input => input.attributes('aria-label') === '对外模型名')!.vm.$emit('update:value', 'ds4')
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await flushPromises()
    expect(page.text()).toContain('请为每个目标选择平台')
    expect(save).not.toHaveBeenCalled()
  })

  it('支持停用和确认删除，加载失败提供重试入口', async () => {
    const save = vi.spyOn(api, 'saveModelMapping').mockResolvedValue({ ...mapping, enabled: false })
    const remove = vi.spyOn(api, 'deleteModelMapping').mockResolvedValue()
    const page = await render([mapping])
    await click('停用')
    expect(save).toHaveBeenCalledWith({ ...mapping, enabled: false })
    page.getComponent(NPopconfirm).vm.$emit('positive-click')
    await vi.waitFor(() => expect(remove).toHaveBeenCalledWith('mapping-id'))
    vi.mocked(api.modelMappings).mockRejectedValue(new Error('目录读取失败'))
    await client!.invalidateQueries({ queryKey: ['model-mappings'] })
    await flushPromises()
    expect(page.text()).toContain('目录读取失败')
    vi.mocked(api.modelMappings).mockResolvedValue([])
    await click('重新加载')
    await vi.waitFor(() => expect(page.find('.mapping-card').exists()).toBe(false))
  })
})
