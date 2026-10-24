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
    { pluginKey: 'plugin-a', platform: 'a', model: 'real-a', enabled: true, retryCount: 1, failureThreshold: 3, cooldownSeconds: 300 },
    { pluginKey: 'plugin-b', platform: 'b', model: 'real-b', enabled: true, retryCount: null, failureThreshold: 3, cooldownSeconds: 300 },
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

function select(label: string) {
  return view!.findAllComponents(NSelect).find(input => input.props('inputProps')?.['aria-label'] === label)!
}
function number(label: string) {
  return view!.findAllComponents(NInputNumber).find(input => input.props('inputProps')?.['aria-label'] === label)!
}

describe('模型映射管理', () => {
  it('新建目标默认 3 轮、5 分钟，并允许为新目标单独设置', async () => {
    await render()
    await click('新建映射')
    expect(number('目标 1 连续失败轮数').props('value')).toBe(3)
    expect(number('目标 1 冷却时间').props('value')).toBe(5)
    expect(select('目标 1 冷却单位').props('value')).toBe(60)
    number('目标 1 连续失败轮数').vm.$emit('update:value', 8)
    await click('添加目标')
    expect(number('目标 2 连续失败轮数').props('value')).toBe(3)
    expect(number('目标 2 冷却时间').props('value')).toBe(5)
  })

  it.each([[3600, 2, 7200], [60, 5, 300], [1, 45, 45]])('按单位 %s 将输入 %s 换算为 %s 秒保存', async (unit, value, seconds) => {
    const write = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(mapping)
    const page = await render([mapping])
    await click('编辑')
    number('目标 1 连续失败轮数').vm.$emit('update:value', 7)
    number('目标 1 冷却时间').vm.$emit('update:value', value)
    select('目标 1 冷却单位').vm.$emit('update:value', unit)
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(write).toHaveBeenCalled())
    expect(write.mock.calls[0]![0].targets).toEqual([
      { ...mapping.targets[0], failureThreshold: 7, cooldownSeconds: seconds }, mapping.targets[1],
    ])
  })

  it.each([[0, 300], [3, 0], [0, 0]])('轮数 %s、冷却秒数 %s 时明确关闭冷却，保存时保留零', async (failureThreshold, cooldownSeconds) => {
    const configured = { ...mapping, targets: [{ ...mapping.targets[0]!, failureThreshold, cooldownSeconds }] }
    const write = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(configured)
    const page = await render([configured])
    expect(page.text()).toContain('不自动冷却')
    await click('编辑')
    expect(page.text()).toContain('此目标不自动冷却。')
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(write).toHaveBeenCalledWith(configured))
  })

  it('编辑回显小时和秒，调整调用顺序后保留各自的冷却规则', async () => {
    const configured = { ...mapping, targets: [
      { ...mapping.targets[0]!, failureThreshold: 2, cooldownSeconds: 7200 },
      { ...mapping.targets[1]!, failureThreshold: 7, cooldownSeconds: 45 },
    ] }
    const write = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(configured)
    const page = await render([configured])
    expect(page.text()).toContain('连续 2 轮失败，冷却 2 小时')
    expect(page.text()).toContain('连续 7 轮失败，冷却 45 秒')
    await click('编辑')
    expect(number('目标 1 冷却时间').props('value')).toBe(2)
    expect(select('目标 1 冷却单位').props('value')).toBe(3600)
    expect(number('目标 2 冷却时间').props('value')).toBe(45)
    expect(select('目标 2 冷却单位').props('value')).toBe(1)
    page.findAllComponents(NButton).find(button => button.attributes('aria-label') === '上移目标 2')!.vm.$emit('click')
    await flushPromises()
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(write).toHaveBeenCalledWith({ ...configured, targets: [...configured.targets].reverse() }))
  })

  it('轮数或时间清空时保留编辑并显示校验错误', async () => {
    const write = vi.spyOn(api, 'saveModelMapping').mockResolvedValue(mapping)
    const page = await render([mapping])
    await click('编辑')
    number('目标 1 连续失败轮数').vm.$emit('update:value', null)
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await flushPromises()
    expect(page.text()).toContain('请填写有效的非负整数轮数和冷却时间')
    number('目标 1 连续失败轮数').vm.$emit('update:value', 0)
    number('目标 1 冷却时间').vm.$emit('update:value', null)
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await flushPromises()
    expect(write).not.toHaveBeenCalled()
    expect(page.find('.mapping-editor').exists()).toBe(true)
  })

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
    select('目标 1 平台').vm.$emit('update:value', 'a')
    await flushPromises()
    select('目标 1 模型').vm.$emit('update:value', model)
    await flushPromises()
    expect(name.props('value')).toBe(expectedName)
    await click('添加目标')
    select('目标 2 平台').vm.$emit('update:value', 'b')
    await flushPromises()
    select('目标 2 模型').vm.$emit('update:value', 'other-model')
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
    select('目标 1 平台').vm.$emit('update:value', 'a')
    await flushPromises()
    select('目标 1 模型').vm.$emit('update:value', 'custom-a')
    expect(page.getComponent(NInputNumber).props()).toMatchObject({ min: 1, max: 35 })
    page.getComponent(NInputNumber).vm.$emit('update:value', 3)
    await click('添加目标')
    expect(page.text()).toContain('此目标最多调用 3 次，包含首次请求。')
    select('目标 2 平台').vm.$emit('update:value', 'b')
    await flushPromises()
    select('目标 2 模型').vm.$emit('update:value', 'custom-b')
    page.findAllComponents(NButton).find(button => button.attributes('aria-label') === '上移目标 2')!.vm.$emit('click')
    await flushPromises()
    page.getComponent(NForm).vm.$emit('submit', new Event('submit'))
    await vi.waitFor(() => expect(save).toHaveBeenCalled())
    expect(save).toHaveBeenCalledWith({ id: '', name: 'ds4', enabled: true, targets: [
      { pluginKey: 'plugin-b', platform: 'b', model: 'custom-b', enabled: true, retryCount: null, failureThreshold: 3, cooldownSeconds: 300 },
      { pluginKey: 'plugin-a', platform: 'a', model: 'custom-a', enabled: true, retryCount: 3, failureThreshold: 3, cooldownSeconds: 300 },
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
