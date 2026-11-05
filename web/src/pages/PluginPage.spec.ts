import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, expect, it, vi } from 'vitest'
import { api } from '@/services/api'
import AdminLayout from '@/layouts/AdminLayout.vue'
import styles from '@/styles.css?raw'
import PluginPage from './PluginPage.vue'

vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => ({ success: vi.fn(), error: vi.fn() }),
}))

let view: VueWrapper | undefined
let client: QueryClient | undefined
let sheet: HTMLStyleElement | undefined
afterEach(() => { view?.unmount(); client?.clear(); sheet?.remove(); document.body.replaceChildren(); vi.restoreAllMocks() })

it.each([false, true])('插件加载与结束状态共用剩余高度链，没有卡片内边距（失败=%s）', async failed => {
  let finish!: () => void
  vi.spyOn(api, 'pluginPage').mockImplementation(() => new Promise<string>((resolve, reject) => {
    finish = () => failed ? reject(new Error('页面不可用')) : resolve('<html><body>插件页面</body></html>')
  }))
  vi.spyOn(api, 'plugins').mockResolvedValue([])
  vi.spyOn(api, 'hostVersion').mockResolvedValue({ version: 'test' })
  vi.spyOn(api, 'appearance').mockResolvedValue({ theme: 'minimal', colorMode: 'light' })
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/plugins/:pluginKey', component: PluginPage },
    { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
  ] })
  await router.push('/plugins/demo')
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  sheet = document.createElement('style')
  sheet.textContent = styles
  document.head.append(sheet)
  view = mount(AdminLayout, { attachTo: document.body, global: { plugins: [router, [VueQueryPlugin, { queryClient: client }]], stubs: { Transition: false } } })
  await flushPromises()
  function expectHeightChain() {
    expect(view!.get('.page-content').element.matches('.page-content:has(> .route-view > .plugin-page-card)')).toBe(true)
    const main = getComputedStyle(view!.get('.page-content').element)
    expect(main.display).toBe('flex')
    expect(main.height).toBe('100%')
    expect(main.paddingTop).toBe('0px')
    expect(main.paddingBottom).toBe('0px')
    expect(main.paddingLeft).toBe('8px')
    const route = getComputedStyle(view!.get('.route-view').element)
    expect(route.flexGrow).toBe('1')
    expect(route.minHeight).toBe('0px')
    expect(getComputedStyle(view!.get('.plugin-page-card').element).height).toBe('100%')
    expect(view!.find('.plugin-page-card .n-card-content').exists()).toBe(false)
  }
  expect(view.find('.plugin-page-state').exists()).toBe(true)
  expectHeightChain()
  finish()
  await flushPromises()
  expectHeightChain()
  expect(view.find('iframe').exists()).toBe(!failed)
  expect(view.find('.plugin-page-error').exists()).toBe(failed)
})

it('注入样式脚本并同步主题，不改变 srcdoc 或重载 iframe，保持原沙箱隔离', async () => {
  vi.spyOn(api, 'pluginPage').mockResolvedValue('<html><head></head><body><input value="draft"></body></html>')
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/plugins/:pluginKey', component: PluginPage }] })
  await router.push('/plugins/demo')
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['appearance'], { theme: 'minimal', colorMode: 'light' })
  view = mount(PluginPage, { attachTo: document.body, global: { plugins: [router, [VueQueryPlugin, { queryClient: client }]] } })
  await flushPromises()
  const frame = view.get('iframe')
  const source = frame.attributes('srcdoc')
  expect(source).toContain('router2api-appearance-ready')
  expect(source).toContain('window.Router2API=')
  expect(frame.attributes('sandbox')).not.toContain('allow-same-origin')
  const post = vi.spyOn((frame.element as HTMLIFrameElement).contentWindow!, 'postMessage')
  await frame.trigger('load')
  expect(post).toHaveBeenLastCalledWith(expect.objectContaining({ type: 'router2api-appearance', appearance: expect.objectContaining({ colorMode: 'light' }) }), '*')
  post.mockClear()
  window.dispatchEvent(new MessageEvent('message', { source: window, data: { type: 'router2api-appearance-ready' } }))
  expect(post).not.toHaveBeenCalled()
  window.dispatchEvent(new MessageEvent('message', { source: (frame.element as HTMLIFrameElement).contentWindow!, data: { type: 'router2api-appearance-ready' } }))
  expect(post).toHaveBeenCalledTimes(1)
  client.setQueryData(['appearance'], { theme: 'default', colorMode: 'dark' })
  await flushPromises()
  expect(post).toHaveBeenLastCalledWith(expect.objectContaining({ appearance: expect.objectContaining({ theme: 'default', colorMode: 'dark' }) }), '*')
  expect(view.get('iframe').element).toBe(frame.element)
  expect(view.get('iframe').attributes('srcdoc')).toBe(source)
})
