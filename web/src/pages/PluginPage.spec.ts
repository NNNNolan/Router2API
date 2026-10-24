import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, expect, it, vi } from 'vitest'
import { api } from '@/services/api'
import PluginPage from './PluginPage.vue'

let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => { view?.unmount(); client?.clear(); vi.restoreAllMocks() })

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
