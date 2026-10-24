import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NButton, NSelect } from 'naive-ui'
import { afterEach, expect, it, vi } from 'vitest'
import { api, type ConfigSnapshot } from '@/services/api'
import ConfigPage from './ConfigPage.vue'

const message = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('naive-ui', async importOriginal => ({ ...await importOriginal<typeof import('naive-ui')>(), useMessage: () => message }))
const initial: ConfigSnapshot = {
  configPath: 'test/config.json', fileExists: true, source: 'test', appearance: { theme: 'default', colorMode: 'light' },
  auth: { adminEnabled: true, sessionLifetimeHours: 12, administratorCount: 1, apiKeyEnabled: true, apiKeyPreview: '********', apiKeyConfigured: true },
  database: { path: 'test.db' }, redis: { connectionString: '', instanceName: 'test:' },
  logging: { dataRetentionDays: 30, cleanupCron: '0 0 3 * * *', minimumPluginLogLevel: 'Information' },
}
let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.clearAllMocks() })

async function render() {
  vi.spyOn(api, 'config').mockResolvedValue(initial)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['appearance'], initial.appearance)
  view = mount(ConfigPage, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  await flushPromises()
  await vi.waitFor(() => expect(view!.findAllComponents(NSelect)).toHaveLength(2))
  view.findAllComponents(NSelect)[0]!.vm.$emit('update:value', 'minimal')
  await flushPromises()
  return view
}

async function save() {
  view!.findAllComponents(NButton).find(button => button.text() === '保存配置')!.vm.$emit('click')
  await flushPromises()
}

it('选择主题后保存到服务器，成功后才更新全局主题', async () => {
  const write = vi.spyOn(api, 'saveConfig').mockResolvedValue({ ...initial, appearance: { theme: 'minimal', colorMode: 'light' } })
  await render()
  expect(client!.getQueryData(['appearance'])).toEqual({ theme: 'default', colorMode: 'light' })
  await save()
  expect(write).toHaveBeenCalledWith(expect.objectContaining({ appearance: { theme: 'minimal' } }))
  expect(client!.getQueryData(['appearance'])).toEqual({ theme: 'minimal', colorMode: 'light' })
  expect(message.success).toHaveBeenCalled()
})

it('保存失败保留编辑选择和已生效的主题，并显示错误', async () => {
  vi.spyOn(api, 'saveConfig').mockRejectedValue(new Error('写入失败'))
  const page = await render()
  await save()
  expect(client!.getQueryData(['appearance'])).toEqual({ theme: 'default', colorMode: 'light' })
  expect(page.findAllComponents(NSelect)[0]!.props('value')).toBe('minimal')
  expect(message.error).toHaveBeenCalledWith('写入失败')
  expect(message.success).not.toHaveBeenCalled()
})
