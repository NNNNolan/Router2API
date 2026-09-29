import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, type PluginDescriptor } from '@/services/api'
import PluginsPage from './PluginsPage.vue'

vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => ({ success: vi.fn(), error: vi.fn() }),
}))

let view: VueWrapper | undefined
let client: QueryClient | undefined
afterEach(() => {
  view?.unmount()
  client?.clear()
  vi.restoreAllMocks()
})

async function render() {
  vi.spyOn(api, 'plugins').mockResolvedValue([])
  vi.spyOn(api, 'pluginRepositories').mockResolvedValue([])
  vi.spyOn(api, 'pluginInstallations').mockResolvedValue([])
  const reload = vi.spyOn(api, 'reloadPlugins').mockResolvedValue([])
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  view = mount(PluginsPage, {
    global: {
      plugins: [[VueQueryPlugin, { queryClient: client }]],
      renderStubDefaultSlot: true,
      stubs: {
        ...Object.fromEntries(['NAlert', 'NCard', 'NCheckbox', 'NDataTable', 'NEmpty', 'NInput', 'NModal',
          'NPopconfirm', 'NSelect', 'NSpin', 'NTabPane', 'NTabs', 'NTag'].flatMap(name => [[name, true], [name.slice(1), true]])),
        Button: { props: ['disabled'], emits: ['click'], template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>' },
      },
    },
  })
  await flushPromises()
  return reload
}

async function select(file: File) {
  const input = view!.get<HTMLInputElement>('#plugin-upload-file')
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  await input.trigger('change')
}

function submit() {
  return view!.findAll('button').find(button => button.text() === '上传、覆盖并加载')!
}

describe('插件 ZIP 上传', () => {
  it('发送文件并刷新列表，不调用全量重载', async () => {
    const reload = await render()
    const upload = vi.spyOn(api, 'uploadPlugin').mockResolvedValue({ pluginKey: 'test', name: 'Test', state: 'Active' } as PluginDescriptor)
    const file = new File(['zip'], 'anything.zip', { type: 'application/zip' })
    await select(file)
    await submit().trigger('click')
    await flushPromises()
    expect(upload).toHaveBeenCalledWith(file)
    expect(api.plugins).toHaveBeenCalledTimes(2)
    expect(reload).not.toHaveBeenCalled()
  })

  it.each(['bad.txt', 'empty.zip'])('拒绝无效文件 %s', async name => {
    await render()
    const upload = vi.spyOn(api, 'uploadPlugin')
    await select(new File(name === 'empty.zip' ? [] : ['x'], name))
    await submit().trigger('click')
    expect(upload).not.toHaveBeenCalled()
    expect(view!.text()).toContain('请选择非空 ZIP')
  })

  it('失败展示服务端错误，保留文件以便重试', async () => {
    await render()
    vi.spyOn(api, 'uploadPlugin').mockRejectedValue(new Error('Plugin activation failed; previous package restored.'))
    await select(new File(['zip'], 'test.zip'))
    await submit().trigger('click')
    await flushPromises()
    expect(view!.text()).toContain('previous package restored')
    expect(submit().attributes('disabled')).toBeUndefined()
  })
})
