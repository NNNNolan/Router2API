import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NButton, NDataTable, NInput, NPagination, NSelect, type DialogOptions } from 'naive-ui'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type Account } from '@/services/api'
import AccountsPage from './AccountsPage.vue'

const message = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
const dialog = vi.hoisted(() => ({ warning: vi.fn() }))
vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => message,
  useDialog: () => dialog,
}))

let view: VueWrapper | undefined
let client: QueryClient | undefined
let confirmation: DialogOptions
const accounts: Account[] = ['a', 'b', 'c'].map(id => ({
  id, pluginKey: `plugin-${id}`, platform: `platform-${id}`, label: `账号 ${id}`,
  enabled: true, credentialKind: 'apiKey', credential: {},
}))

beforeEach(() => {
  dialog.warning.mockImplementation((options: DialogOptions) => {
    confirmation = options
    return options
  })
})
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.clearAllMocks() })

async function render(initial = accounts) {
  let stored = [...initial]
  vi.spyOn(api, 'accounts').mockImplementation(async (params = {}) => {
    const filtered = stored.filter(account => (!params.platform || account.platform === params.platform)
      && (!params.state || params.state === 'Active')
      && (!params.keyword || account.label?.includes(params.keyword)))
    const page = params.page ?? 1
    const pageSize = params.pageSize ?? 20
    return { items: filtered.slice((page - 1) * pageSize, page * pageSize), page, pageSize, total: filtered.length, totalPages: Math.ceil(filtered.length / pageSize) }
  })
  vi.spyOn(api, 'platforms').mockResolvedValue([])
  vi.spyOn(api, 'deleteAccount').mockImplementation(async (id, pluginKey) => {
    stored = stored.filter(account => account.id !== id || account.pluginKey !== pluginKey)
  })
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(AccountsPage, { global: {
    plugins: [[VueQueryPlugin, { queryClient: client }]],
    renderStubDefaultSlot: true,
    stubs: Object.fromEntries(['NForm', 'NFormItem', 'NInput', 'NModal', 'NPagination', 'NSelect', 'ResponsiveFilterPanel'].map(name => [name, true])),
  } })
  await flushPromises()
  return view
}

function batchDelete() {
  return view!.findAllComponents(NButton).find(button => button.text() === '批量删除')!
}

async function selectAll() {
  await view!.get('thead [role="checkbox"]').trigger('click')
  await flushPromises()
}

async function confirm() {
  await confirmation.onPositiveClick!(new MouseEvent('click'))
  await flushPromises()
}

describe('账号多选与批量删除', () => {
  it('支持单选、半选、全选当前页及取消全选，未选中时禁用删除', async () => {
    const page = await render()
    expect(batchDelete().props('disabled')).toBe(true)
    await page.get('tbody [role="checkbox"]').trigger('click')
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual(['a'])
    expect(page.get('thead [role="checkbox"]').attributes('aria-checked')).toBe('mixed')
    expect(page.text()).toContain('已选 1 个账号')
    expect(batchDelete().props('disabled')).toBe(false)
    await selectAll()
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual(['a', 'b', 'c'])
    expect(page.get('thead [role="checkbox"]').attributes('aria-checked')).toBe('true')
    await selectAll()
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual([])
    expect(batchDelete().props('disabled')).toBe(true)
  })

  it.each(['page', 'pageSize', 'platform', 'state', 'keyword'])('%s 改变时清空选择，不携带旧页或旧筛选的账号', async change => {
    const page = await render()
    await selectAll()
    if (change === 'page' || change === 'pageSize') {
      page.getComponent(NPagination).vm.$emit(`update:${change === 'page' ? 'page' : 'page-size'}`, change === 'page' ? 2 : 50)
    } else if (change === 'keyword') {
      page.getComponent(NInput).vm.$emit('update:value', '账号 a')
    } else {
      page.findAllComponents(NSelect)[change === 'platform' ? 0 : 1].vm.$emit('update:value', change === 'platform' ? 'platform-a' : 'Active')
    }
    await flushPromises()
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual([])
    expect(batchDelete().props('disabled')).toBe(true)
    expect(api.deleteAccount).not.toHaveBeenCalled()
  })

  it('确认前不发送删除，确认后仅删除所选账号并携带所属插件，完成后刷新', async () => {
    const page = await render()
    await page.get('tbody [role="checkbox"]').trigger('click')
    await batchDelete().trigger('click')
    expect(confirmation.content).toContain('账号 a')
    expect(api.deleteAccount).not.toHaveBeenCalled()
    await confirm()
    expect(api.deleteAccount).toHaveBeenCalledExactlyOnceWith('a', 'plugin-a')
    expect(api.accounts).toHaveBeenCalledTimes(2)
    expect(page.getComponent(NDataTable).props('data')!.map(account => account.id)).toEqual(['b', 'c'])
    expect(batchDelete().props('disabled')).toBe(true)
    expect(message.success).toHaveBeenCalledWith('账号已删除')
  })

  it('部分删除失败时显示成功和失败数量与原因，仅保留失败项选中以便重试', async () => {
    const page = await render()
    vi.mocked(api.deleteAccount).mockRejectedValueOnce(new Error('账号 a 删除失败'))
    await selectAll()
    await batchDelete().trigger('click')
    expect(confirmation.content).toContain('3 个账号')
    await confirm()
    expect(api.deleteAccount).toHaveBeenCalledTimes(3)
    expect(api.deleteAccount).toHaveBeenCalledWith('b', 'plugin-b')
    expect(api.deleteAccount).toHaveBeenCalledWith('c', 'plugin-c')
    expect(message.error).toHaveBeenCalledWith('已删除 2 个账号，1 个删除失败：账号 a 删除失败')
    expect(message.success).not.toHaveBeenCalled()
    expect(page.getComponent(NDataTable).props('data')!.map(account => account.id)).toEqual(['a'])
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual(['a'])
    expect(batchDelete().props('disabled')).toBe(false)
    await batchDelete().trigger('click')
    await confirm()
    expect(page.getComponent(NDataTable).props('data')).toEqual([])
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual([])
  })

  it('删除过程中显示加载状态、禁用重复操作并固定本次确认的账号', async () => {
    const page = await render()
    let finish!: () => void
    vi.mocked(api.deleteAccount).mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve }))
    await selectAll()
    await batchDelete().trigger('click')
    const pending = confirmation.onPositiveClick!(new MouseEvent('click'))
    await flushPromises()
    expect(batchDelete().props('loading')).toBe(true)
    expect(batchDelete().props('disabled')).toBe(true)
    expect(confirmation.loading).toBe(true)
    expect(confirmation.negativeButtonProps?.disabled).toBe(true)
    await selectAll()
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual(['a', 'b', 'c'])
    expect(await confirmation.onPositiveClick!(new MouseEvent('click'))).toBe(false)
    expect(api.deleteAccount).toHaveBeenCalledTimes(3)
    finish()
    await pending
    await flushPromises()
    expect(batchDelete().props('loading')).toBe(false)
    expect(message.success).toHaveBeenCalledWith('已删除 3 个账号')
  })

  it('末页删空后回到有效页，全选不会删除其他页', async () => {
    const initial = Array.from({ length: 21 }, (_, index) => ({ ...accounts[0], id: `account-${index}`, label: `账号 ${index}` }))
    const page = await render(initial)
    page.getComponent(NPagination).vm.$emit('update:page', 2)
    await flushPromises()
    await selectAll()
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual(['account-20'])
    await batchDelete().trigger('click')
    await confirm()
    expect(api.deleteAccount).toHaveBeenCalledExactlyOnceWith('account-20', 'plugin-a')
    expect(page.getComponent(NPagination).props('page')).toBe(1)
    expect(page.getComponent(NDataTable).props('data')).toHaveLength(20)
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual([])
  })

  it('原有单个删除同步清理已选账号，空列表无法批量删除', async () => {
    const page = await render([accounts[0]])
    await selectAll()
    await page.get('tbody').findAll('button').find(button => button.text() === '删除')!.trigger('click')
    expect(api.deleteAccount).not.toHaveBeenCalled()
    await confirm()
    expect(api.deleteAccount).toHaveBeenCalledExactlyOnceWith('a', 'plugin-a')
    expect(page.getComponent(NDataTable).props('checkedRowKeys')).toEqual([])
    expect(page.text()).toContain('还没有账号')
    expect(batchDelete().props('disabled')).toBe(true)
  })
})
