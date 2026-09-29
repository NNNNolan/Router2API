// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'

afterEach(() => vi.unstubAllGlobals())

describe('测试窗口原始响应', () => {
  it.each([
    [200, '{ "id": 9007199254740993, "content": "**原始内容**" }\n'],
    [403, '{"error":{"type":"RegionError","message":"not available","details":{"region":"x"}}}'],
    [500, 'upstream failed\ntry later\n'],
    [502, '<html><body>Bad Gateway</body></html>'],
    [200, '"JSON 字符串"'],
    [200, 'null'],
    [204, ''],
  ])('HTTP %i 的正文不解析、不格式化', async (status, body) => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(status === 204 ? null : body, { status }))
    vi.stubGlobal('fetch', fetchMock)
    const payload = { model: 'test', stream: false }
    const result = await api.v1Request('/v1/chat/completions', payload, 'test-key')
    expect(result).toEqual({ status, body })
    const [path, init] = fetchMock.mock.calls[0]!
    expect(path).toBe('/v1/chat/completions')
    expect(init.credentials).toBe('include')
    expect(init.headers.get('Authorization')).toBe('Bearer test-key')
    expect(init.headers.get('Content-Type')).toBe('application/json')
    expect(init.body).toBe(JSON.stringify(payload))
  })

  it('保留网络异常，不伪造接口响应', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    await expect(api.v1Request('/v1/messages', {}, 'test-key')).rejects.toThrow('Failed to fetch')
  })
})

describe('订阅周期兼容', () => {
  it.each([
    [{ refreshIntervalMinutes: 30 }, 1800],
    [{ refreshIntervalMinutes: 1, refreshIntervalSeconds: 30 }, 30],
    [{}, 3600],
  ])('优先秒字段，兼容旧分钟响应 %j', async (body, seconds) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify([body]))))
    expect((await api.subscriptions())[0]!.refreshIntervalSeconds).toBe(seconds)
  })
})

it('插件上传发送 multipart 文件并保留 CSRF，不手动设置 multipart Content-Type', async () => {
  vi.stubGlobal('document', { cookie: 'router_admin_csrf=upload-token' })
  const fetchMock = vi.fn().mockResolvedValue(new Response('{"pluginKey":"test","state":"Active"}'))
  vi.stubGlobal('fetch', fetchMock)
  const file = new File(['zip'], 'test.zip', { type: 'application/zip' })
  await api.uploadPlugin(file)
  const [path, init] = fetchMock.mock.calls[0]!
  expect(path).toBe('/api/admin/plugins/upload')
  expect(init.body).toBeInstanceOf(FormData)
  expect(init.body.get('file').name).toBe('test.zip')
  expect(init.headers.get('X-CSRF-Token')).toBe('upload-token')
  expect(init.headers.has('Content-Type')).toBe(false)
})
