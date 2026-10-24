import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, expect, it, vi } from 'vitest'
import { api, type AppearanceSettings, type ConfigSnapshot } from '@/services/api'
import AppearanceModeToggle from './AppearanceModeToggle.vue'

const message = vi.hoisted(() => ({ error: vi.fn() }))
vi.mock('naive-ui', async importOriginal => ({ ...await importOriginal<typeof import('naive-ui')>(), useMessage: () => message }))
let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); vi.clearAllMocks() })

function render(appearance: AppearanceSettings) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['appearance'], appearance)
  view = mount(AppearanceModeToggle, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  return view
}
function saved(appearance: AppearanceSettings): ConfigSnapshot {
  return {
    appearance, configPath: 'test.json', fileExists: true, source: 'test',
    auth: { adminEnabled: true, sessionLifetimeHours: 12, administratorCount: 1, apiKeyEnabled: true, apiKeyPreview: '', apiKeyConfigured: false },
    database: { path: 'test.db' }, redis: { connectionString: '', instanceName: '' },
    logging: { dataRetentionDays: 30, cleanupCron: '', minimumPluginLogLevel: 'Information' },
  }
}

it.each(['minimal', 'default'] as const)('%s 配色切换明暗，成功后更新且只提交明暗模式', async theme => {
  const write = vi.spyOn(api, 'saveConfig').mockResolvedValueOnce(saved({ theme, colorMode: 'dark' }))
    .mockResolvedValueOnce(saved({ theme, colorMode: 'light' }))
  const page = render({ theme, colorMode: 'light' })
  await page.get('button[aria-label="切换到黑夜模式"]').trigger('click')
  await flushPromises()
  expect(write).toHaveBeenLastCalledWith({ appearance: { colorMode: 'dark' } })
  expect(client!.getQueryData(['appearance'])).toEqual({ theme, colorMode: 'dark' })
  await page.get('button[aria-label="切换到日间模式"]').trigger('click')
  await flushPromises()
  expect(write).toHaveBeenLastCalledWith({ appearance: { colorMode: 'light' } })
  expect(client!.getQueryData(['appearance'])).toEqual({ theme, colorMode: 'light' })
})

it('保存失败保留当前模式并明确显示错误', async () => {
  vi.spyOn(api, 'saveConfig').mockRejectedValue(new Error('写入失败'))
  const page = render({ theme: 'minimal', colorMode: 'light' })
  await page.get('button').trigger('click')
  await flushPromises()
  expect(client!.getQueryData(['appearance'])).toEqual({ theme: 'minimal', colorMode: 'light' })
  expect(message.error).toHaveBeenCalledWith('写入失败')
})
