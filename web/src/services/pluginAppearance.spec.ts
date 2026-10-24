import { afterEach, expect, it, vi } from 'vitest'
import { pluginAppearanceScript, type PluginAppearance } from './pluginAppearance'

let frame: HTMLIFrameElement | undefined
const dark: PluginAppearance = { theme: 'minimal', colorMode: 'dark', colors: {
  background: '#101114', surface: '#18191d', text: '#d0d5dd', muted: '#98a2b3', border: '#343741',
} }
afterEach(() => { frame?.contentWindow?.close(); frame?.remove(); vi.restoreAllMocks() })

function page() {
  frame = document.createElement('iframe')
  document.body.append(frame)
  const win = frame.contentWindow! as Window & typeof globalThis
  win.document.open()
  win.document.write(`<!doctype html><html><head><style>
    body { background: white; color: #222; }
    .panel { background: #fafafa; color: #333; border: 1px solid #ddd; }
    .panel[data-state="error"] { color: rgb(220, 38, 38); }
    .error { color: rgb(220, 38, 38); }
  </style></head><body>
    <div class="panel" id="panel"><span id="label">模型</span><input id="input" value="保留输入"></div>
    <div id="inline" style="background-color: white !important; color: black !important">内联样式</div>
    <span class="error" id="status">失败</span>
    <span id="disabled" style="color: rgba(0, 0, 0, .38)">禁用提示</span>
    <div id="yellow" style="background-color: yellow; color: black">彩色提示</div>
    <svg style="color: black"><path id="icon" fill="red" /></svg>
  </body></html>`)
  win.document.close()
  expect(win.eval('document')).toBe(win.document)
  win.eval(pluginAppearanceScript.replace(/^<script>|<\/script>$/g, ''))
  const send = (appearance = dark, source: Window | null = win.parent) => win.dispatchEvent(new win.MessageEvent('message', {
    source, data: { type: 'router2api-appearance', appearance },
  }))
  const get = (id: string) => win.document.getElementById(id)!
  return { win, send, get }
}

it('覆盖样式表及内联 important 的浅色底和深色字，保留状态色、彩色提示与图标', async () => {
  const { get, send } = page()
  send()
  await vi.waitFor(() => expect(frame!.contentDocument!.documentElement.dataset.router2apiColorMode).toBe('dark'))
  await vi.waitFor(() => expect(get('inline').style.backgroundColor).toBe('rgb(24, 25, 29)'))
  expect(get('inline').style.getPropertyPriority('background-color')).toBe('important')
  expect(get('label').style.color).toBe('rgb(208, 213, 221)')
  expect(get('panel').style.borderTopColor).toBe('rgb(52, 55, 65)')
  expect(get('status').style.color).toBe('')
  expect(get('disabled').style.color).toBe('rgb(152, 162, 179)')
  expect(get('yellow').style.color).toBe('black')
  expect(get('icon').getAttribute('fill')).toBe('red')
})

it('切回日间精确恢复原样式与优先级，保留插件主动修改及输入值', async () => {
  const { get, send } = page()
  send()
  await vi.waitFor(() => expect(get('inline').style.color).toBe('rgb(208, 213, 221)'))
  get('panel').style.color = 'rgb(220, 38, 38)'
  send({ ...dark, colorMode: 'light' })
  await vi.waitFor(() => expect(get('inline').style.color).toBe('black'))
  expect(get('inline').style.getPropertyPriority('color')).toBe('important')
  expect(get('inline').style.backgroundColor).toBe('white')
  expect(get('panel').style.backgroundColor).toBe('')
  expect(get('panel').style.color).toBe('rgb(220, 38, 38)')
  expect((get('input') as HTMLInputElement).value).toBe('保留输入')
})

it('动态内容与样式变化继续适配，切换主题更新同一批变量', async () => {
  const { win, get, send } = page()
  send()
  await vi.waitFor(() => expect(get('panel').style.backgroundColor).toBe('rgb(24, 25, 29)'))
  const item = win.document.createElement('div')
  item.id = 'dynamic'
  item.className = 'panel'
  win.document.body.append(item)
  await vi.waitFor(() => expect(item.style.backgroundColor).toBe('rgb(24, 25, 29)'))
  item.dataset.state = 'error'
  await vi.waitFor(() => expect(item.style.color).toBe(''))
  send({ ...dark, theme: 'default', colors: { ...dark.colors, surface: '#202228' } })
  await vi.waitFor(() => expect(win.document.documentElement.dataset.router2apiTheme).toBe('default'))
  expect(win.document.documentElement.style.getPropertyValue('--router2api-surface')).toBe('#202228')
})

it('拒绝非父窗口的消息和无效模式', async () => {
  const { win, send } = page()
  send(dark, null)
  send({ ...dark, colorMode: 'invalid' as 'dark' })
  await new Promise(resolve => win.requestAnimationFrame(resolve))
  expect(win.document.documentElement.dataset.router2apiColorMode).toBeUndefined()
})
