import { flushPromises, shallowMount, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/services/api'
import TestPage from './TestPage.vue'

vi.mock('naive-ui', async importOriginal => ({
  ...await importOriginal<typeof import('naive-ui')>(),
  useMessage: () => ({ success: vi.fn() }),
}))

let wrapper: VueWrapper | undefined
// jsdom 未实现 Object URL；音频面板卸载时会回收 Blob URL，这里补一个可断言的实现。
const revokeObjectURL = vi.fn()
Object.defineProperty(URL, 'revokeObjectURL', { value: revokeObjectURL, configurable: true, writable: true })
afterEach(() => {
  wrapper?.unmount()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  revokeObjectURL.mockClear()
})

describe('测试窗口', () => {
  it.each([
    [200, '{ "choices": [{"message": {"content": "**不要渲染 Markdown**"}}], "id": 9007199254740993 }\n',
      '{\n  "choices": [\n    {\n      "message": {\n        "content": "**不要渲染 Markdown**"\n      }\n    }\n  ],\n  "id": 9007199254740993\n}'],
    [403, String.raw`{"error":{"type":"RegionError","message":"\u5730\u533a\u9519\u8bef"}}`,
      '{\n  "error": {\n    "type": "RegionError",\n    "message": "地区错误"\n  }\n}'],
    [502, '<script>alert("不执行 HTML")</script>\nBad Gateway', '<script>alert("不执行 HTML")</script>\nBad Gateway'],
    [200, '{"text":"<script>alert(1)</script>"}', '{\n  "text": "<script>alert(1)</script>"\n}'],
    [204, '', ''],
  ])('完整显示 HTTP %i 正文，仅美化 JSON', async (status, body, expected) => {
    vi.spyOn(api, 'models').mockResolvedValue({ data: [{ id: 'test', displayName: 'test' }] })
    vi.spyOn(api, 'rawApiKey').mockResolvedValue({ key: 'test-key' })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(status === 204 ? null : body, { status })))
    wrapper = shallowMount(TestPage, {
      global: {
        renderStubDefaultSlot: true,
        stubs: {
          Button: {
            props: ['disabled'],
            emits: ['click'],
            template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>',
          },
        },
      },
    })
    await flushPromises()
    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(wrapper.get('.response-text').element.textContent).toBe(expected)
    expect(wrapper.get('.response-toolbar').text()).toContain(`HTTP ${status}`)
    expect(wrapper.find('.response-markdown').exists()).toBe(false)
    expect(wrapper.find('script').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('[object Object]')
  })

  it('音频响应显示播放器与下载链接，不再渲染正文', async () => {
    vi.spyOn(api, 'models').mockResolvedValue({ data: [{ id: 'doubaotts/taozi', displayName: 'taozi' }] })
    vi.spyOn(api, 'rawApiKey').mockResolvedValue({ key: 'test-key' })
    vi.spyOn(api, 'v1Request').mockResolvedValue({
      status: 200,
      body: '',
      contentType: 'audio/mpeg',
      audio: { url: 'blob:speech', size: 2048 },
    })
    wrapper = shallowMount(TestPage, {
      global: {
        renderStubDefaultSlot: true,
        stubs: {
          Button: {
            props: ['disabled'],
            emits: ['click'],
            template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>',
          },
        },
      },
    })
    await flushPromises()
    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(wrapper.get('audio').attributes('src')).toBe('blob:speech')
    expect(wrapper.get('audio').attributes('controls')).toBeDefined()
    expect(wrapper.get('.download-link').attributes()).toMatchObject({ href: 'blob:speech', download: 'speech.mp3' })
    expect(wrapper.get('.audio-meta').text()).toContain('2.0 KB')
    expect(wrapper.find('.response-text').exists()).toBe(false)

    wrapper.unmount()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:speech')
    wrapper = undefined
  })
})
