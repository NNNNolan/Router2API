import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, type PluginDescriptor } from '@/services/api'
import PluginsPage from '@/pages/PluginsPage.vue'
import AdminLayout from './AdminLayout.vue'

vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => ({ success: vi.fn(), error: vi.fn() }),
}))

function plugin(pluginKey: string, state = 'Active', hasMainPage = true): PluginDescriptor {
  return {
    pluginKey, name: pluginKey, state, hasMainPage,
    version: '1.0.0', runtime: 'dotnet', description: null, directoryPath: '', loadedAt: '', inFlight: 0,
    mainPageTitle: hasMainPage ? `${pluginKey} 页面` : null, mainPageVersion: null, tasks: [], routes: [],
  }
}

let wrapper: VueWrapper | undefined
let queryClient: QueryClient | undefined

afterEach(() => {
  wrapper?.unmount()
  queryClient?.clear()
  vi.restoreAllMocks()
})

async function renderPlugins(initial: PluginDescriptor[], version: string | Error = '2.0.3') {
  let plugins = initial
  const getVersion = vi.spyOn(api, 'hostVersion')
  if (version instanceof Error) getVersion.mockRejectedValue(version)
  else getVersion.mockResolvedValue({ version })
  vi.spyOn(api, 'plugins').mockImplementation(async () => plugins)
  vi.spyOn(api, 'pluginRepositories').mockResolvedValue([])
  vi.spyOn(api, 'pluginInstallations').mockResolvedValue([])
  const toggle = vi.spyOn(api, 'setPluginEnabled').mockImplementation(async (key, enabled) => {
    // 保留旧版服务端的 hasMainPage 标记，验证前端也会按运行状态过滤。
    plugins = plugins.map(item => item.pluginKey === key ? { ...item, state: enabled ? 'Active' : 'Disabled' } : item)
    return plugins.find(item => item.pluginKey === key)!
  })
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/plugins', component: PluginsPage },
      { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  })
  await router.push('/plugins')
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  wrapper = mount(AdminLayout, {
    global: {
      plugins: [router, [VueQueryPlugin, { queryClient }]],
      renderStubDefaultSlot: true,
      stubs: {
        ...Object.fromEntries([
          'NAvatar', 'NDrawer', 'NDrawerContent', 'NIcon', 'NLayout', 'NLayoutContent', 'NLayoutHeader', 'NLayoutSider',
          'NAlert', 'NCard', 'NCheckbox', 'NDataTable', 'NEmpty', 'NInput', 'NModal', 'NPopconfirm', 'NSelect',
          'NSpin', 'NTabPane', 'NTabs', 'NTag',
        ].flatMap(name => [[name, true], [name.slice(1), true]])),
        Button: {
          props: ['disabled'],
          emits: ['click'],
          template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>',
        },
      },
    },
  })
  await flushPromises()
  await vi.waitFor(() => expect(wrapper!.findAll('.plugin-card')).toHaveLength(initial.length))
  return { view: wrapper, toggle }
}

function expectMenus(keys: string[]) {
  for (const label of ['主导航', '移动端主导航']) {
    const nav = wrapper!.get(`nav[aria-label="${label}"]`)
    expect(nav.findAll('.nav-sublink').map(link => link.attributes('href'))).toEqual(keys.map(key => `/plugins/${key}`))
    expect(nav.find('a[href="/plugins"]').exists()).toBe(true)
  }
}

describe('插件侧栏菜单', () => {
  it.each(['2.0.3', '2.1.0-rc.1'])('桌面和移动端显示后端返回的宿主版本 %s，不使用插件版本', async version => {
    const { view } = await renderPlugins([plugin('example')], version)
    await vi.waitFor(() => expect(view.findAll('[aria-label="宿主版本"]').map(label => label.text())).toEqual([`v${version}`, `v${version}`]))
    expect(api.hostVersion).toHaveBeenCalledTimes(1)
    expect(view.text()).not.toContain('MVP · v0.1')
  })

  it('版本接口失败时明确提示，不伪造版本且不影响导航', async () => {
    const { view } = await renderPlugins([plugin('active')], new Error('版本接口不可用'))
    await vi.waitFor(() => expect(view.findAll('[aria-label="宿主版本"]').map(label => label.text())).toEqual(['版本获取失败', '版本获取失败']))
    expectMenus(['active'])
  })

  it('只展示正在运行且提供主页面的插件', async () => {
    await renderPlugins([
      plugin('active'), plugin('disabled', 'Disabled'), plugin('draining', 'Draining'),
      plugin('failed', 'Failed'), plugin('headless', 'Active', false),
    ])

    expectMenus(['active'])
  })

  it.each([true, false])('切换插件后同步刷新两端菜单，没有页面时不新增入口（hasPage=%s）', async hasPage => {
    const { view, toggle } = await renderPlugins([plugin('target', 'Active', hasPage), plugin('other')])
    const activeMenus = hasPage ? ['target', 'other'] : ['other']
    expectMenus(activeMenus)

    await view.get('.plugin-card').findAll('button').find(button => button.text() === '禁用')!.trigger('click')
    await vi.waitFor(() => expect(view.get('.plugin-card').text()).toContain('已禁用'))
    expect(toggle).toHaveBeenLastCalledWith('target', false)
    expectMenus(['other'])

    await view.get('.plugin-card').findAll('button').find(button => button.text() === '启用')!.trigger('click')
    await vi.waitFor(() => expect(view.get('.plugin-card').text()).toContain('运行中'))
    expect(toggle).toHaveBeenLastCalledWith('target', true)
    expectMenus(activeMenus)
  })
})
