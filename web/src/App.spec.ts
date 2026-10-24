import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { NConfigProvider } from 'naive-ui'
import { afterEach, expect, it, vi } from 'vitest'
import { api } from '@/services/api'
import App from './App.vue'

let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks(); delete document.documentElement.dataset.theme })

async function render() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(App, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]], stubs: { RouterView: true } } })
  await flushPromises()
  return view
}

it('读取已保存的主题，同步组件配色和页面样式，并支持切换蓝色主题', async () => {
  vi.spyOn(api, 'appearance').mockResolvedValue({ theme: 'minimal' })
  const page = await render()
  await vi.waitFor(() => expect(document.documentElement.dataset.theme).toBe('minimal'))
  expect(page.getComponent(NConfigProvider).props('themeOverrides')?.common).toMatchObject({ primaryColor: '#18181B', bodyColor: '#FFFFFF' })
  client!.setQueryData(['appearance'], { theme: 'default' })
  await flushPromises()
  expect(document.documentElement.dataset.theme).toBe('default')
  expect(page.getComponent(NConfigProvider).props('themeOverrides')?.common?.primaryColor).toBe('#2563EB')
})

it('配置加载期间默认黑白，读取到已保存的蓝色选择后保留该选择', async () => {
  let resolveTheme!: (value: { theme: 'default' }) => void
  vi.spyOn(api, 'appearance').mockReturnValue(new Promise(resolve => { resolveTheme = resolve }))
  const page = await render()
  expect(document.documentElement.dataset.theme).toBe('minimal')
  expect(page.getComponent(NConfigProvider).props('themeOverrides')?.common?.primaryColor).toBe('#18181B')
  resolveTheme({ theme: 'default' })
  await vi.waitFor(() => expect(document.documentElement.dataset.theme).toBe('default'))
  expect(page.getComponent(NConfigProvider).props('themeOverrides')?.common?.primaryColor).toBe('#2563EB')
})

it('主题读取失败明确提示，并允许重新读取', async () => {
  const read = vi.spyOn(api, 'appearance').mockRejectedValueOnce(new Error('unavailable')).mockResolvedValue({ theme: 'minimal' })
  const page = await render()
  await vi.waitFor(() => expect(page.text()).toContain('主题设置加载失败'))
  await page.get('button').trigger('click')
  await vi.waitFor(() => expect(document.documentElement.dataset.theme).toBe('minimal'))
  expect(read).toHaveBeenCalledTimes(2)
  expect(page.text()).not.toContain('主题设置加载失败')
})
