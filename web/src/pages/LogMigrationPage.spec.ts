import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NButton } from 'naive-ui'
import { afterEach, expect, it, vi } from 'vitest'
import { api, type LogMigrationStatus } from '@/services/api'
import LogMigrationPage from './LogMigrationPage.vue'

const message = vi.hoisted(() => ({ error: vi.fn() }))
vi.mock('naive-ui', async importOriginal => ({ ...await importOriginal<typeof import('naive-ui')>(), useMessage: () => message }))
const status = (overrides: Partial<LogMigrationStatus> = {}): LogMigrationStatus => ({ status: 'idle', phase: 'idle', sourcePath: 'old.db', targetPath: 'logs.db', restartRequired: false, totalRows: 0, copiedRows: 0, verifiedRows: 0, totalTables: 0, deletedTables: 0, currentTable: null, progressPercent: 0, error: null, startedAt: null, finishedAt: null, ...overrides })
let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.clearAllMocks(); vi.useRealTimers() })

async function render(initial: LogMigrationStatus) {
  vi.spyOn(api, 'logMigrationStatus').mockResolvedValue(initial)
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  view = mount(LogMigrationPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  await flushPromises()
  return view
}

function button() { return view!.findAllComponents(NButton).find(item => /迁移/.test(item.text()))! }

it('空闲且没有旧表时显示无需迁移', async () => {
  const page = await render(status())
  expect(page.text()).toContain('无需迁移')
  expect(button().props('disabled')).toBe(true)
})

it('存在空旧表时仍允许启动清理', async () => {
  await render(status({ totalTables: 2 }))
  expect(button().props('disabled')).toBe(false)
})

it('允许迁移空旧表，并显示复制与校验进度', async () => {
  const page = await render(status({ totalTables: 2, totalRows: 0, phase: 'copying', status: 'running', currentTable: 'RequestLogs' }))
  expect(page.text()).toContain('RequestLogs')
  expect(page.text()).toContain('已复制 0 / 0 行')
  expect(button().props('disabled')).toBe(true)
})

it('运行期间每秒刷新状态；页面重挂载后沿用缓存状态继续轮询', async () => {
  vi.useFakeTimers()
  const get = vi.spyOn(api, 'logMigrationStatus').mockResolvedValue(status({ status: 'running', phase: 'copying', totalRows: 4, totalTables: 1 }))
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  view = mount(LogMigrationPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  await flushPromises()
  view.unmount()
  client.setQueryData(['log-migration'], status({ status: 'running', phase: 'copying', totalRows: 4, totalTables: 1 }))
  view = mount(LogMigrationPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  await flushPromises()
  expect(get).toHaveBeenCalledTimes(2)
  await vi.advanceTimersByTimeAsync(1000)
  await flushPromises()
  expect(get).toHaveBeenCalledTimes(3)
})

it('失败状态可重试，启动错误会显示并重新读取服务端状态', async () => {
  await render(status({ status: 'failed', phase: 'failed', totalTables: 1, error: '校验不一致' }))
  expect(view!.text()).toContain('校验不一致')
  const start = vi.spyOn(api, 'logMigrationStart').mockRejectedValue(new Error('服务忙'))
  vi.spyOn(api, 'logMigrationStatus').mockResolvedValue(status({ status: 'failed', phase: 'failed', totalTables: 1, error: '校验不一致' }))
  await button().trigger('click')
  await flushPromises()
  expect(start).toHaveBeenCalledOnce()
  expect(message.error).toHaveBeenCalledWith('服务忙')
})

it('完成后使日志、分析、指标、隐私和链路查询失效', async () => {
  const invalidate = vi.spyOn(QueryClient.prototype, 'invalidateQueries')
  await render(status({ status: 'completed', phase: 'completed', totalTables: 2, deletedTables: 2, totalRows: 8, copiedRows: 8, verifiedRows: 8, progressPercent: 100 }))
  await flushPromises()
  const keys = invalidate.mock.calls.map(([filters]) => typeof filters === 'function' ? filters().queryKey : filters?.queryKey)
  expect(keys).toEqual(expect.arrayContaining([['logs'], ['plugin-logs'], ['task-logs'], ['analytics-overview'], ['analytics'], ['metrics'], ['privacy-summary'], ['privacy-events'], ['trace-topology'], ['trace-requests'], ['trace-request']]))
})

it('启动请求等待期间禁用按钮并阻止重复提交，成功后恢复运行态轮询', async () => {
  vi.useFakeTimers()
  const get = vi.spyOn(api, 'logMigrationStatus').mockResolvedValue(status({ totalTables: 1, totalRows: 4 }))
  await render(status({ totalTables: 1, totalRows: 4 }))
  let finish!: (result: LogMigrationStatus) => void
  const start = vi.spyOn(api, 'logMigrationStart').mockReturnValue(new Promise(resolve => { finish = resolve }))
  button().vm.$emit('click')
  await nextTick()
  expect(button().props('disabled')).toBe(true)
  button().vm.$emit('click')
  expect(start).toHaveBeenCalledOnce()
  finish(status({ status: 'running', phase: 'copying', totalTables: 1, totalRows: 4 }))
  await flushPromises()
  expect(button().props('disabled')).toBe(true)
  await vi.advanceTimersByTimeAsync(1000)
  await flushPromises()
  expect(get).toHaveBeenCalledTimes(2)
})

it('状态完成后停止轮询，卸载时也会清除轮询', async () => {
  vi.useFakeTimers()
  const get = vi.spyOn(api, 'logMigrationStatus')
    .mockResolvedValueOnce(status({ status: 'running', phase: 'copying', totalTables: 1 }))
    .mockResolvedValueOnce(status({ status: 'completed', phase: 'completed', totalTables: 1, deletedTables: 1 }))
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  view = mount(LogMigrationPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  await flushPromises()
  await vi.advanceTimersByTimeAsync(1000)
  await flushPromises()
  await vi.advanceTimersByTimeAsync(3000)
  expect(get).toHaveBeenCalledTimes(2)
  get.mockClear()
  client.setQueryData(['log-migration'], status({ status: 'running', phase: 'copying', totalTables: 1 }))
  await flushPromises()
  view.unmount()
  await vi.advanceTimersByTimeAsync(3000)
  expect(get).not.toHaveBeenCalled()
})

it('需要重启时禁用迁移按钮', async () => {
  await render(status({ restartRequired: true, totalTables: 1, totalRows: 10 }))
  expect(view!.text()).toContain('需要重启宿主')
  expect(button().props('disabled')).toBe(true)
})
