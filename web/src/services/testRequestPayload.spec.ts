import { describe, expect, it } from 'vitest'
import { buildTestPayload, endpointOptions, endpointPaths, type SpeechOptions } from './testRequestPayload'

const defaults: SpeechOptions = { voice: '', responseFormat: '', speed: 1 }

describe('测试窗口请求体', () => {
  it('每个端点都有公开路径与下拉选项', () => {
    for (const option of endpointOptions) {
      expect(endpointPaths[option.value]).toMatch(/^\/v1\//)
    }
    expect(endpointPaths['audio-speech']).toBe('/v1/audio/speech')
  })

  it('聊天端点保留 model/stream/messages', () => {
    expect(buildTestPayload('chat-completions', ' doubaotts/taozi ', ' 你好 ')).toEqual({
      model: 'doubaotts/taozi',
      stream: false,
      messages: [{ role: 'user', content: '你好' }],
    })
  })

  it('Responses 与 Anthropic Messages 使用各自字段', () => {
    expect(buildTestPayload('responses', 'm', 'hi')).toEqual({ model: 'm', stream: false, input: 'hi' })
    expect(buildTestPayload('anthropic-messages', 'm', 'hi')).toEqual({
      model: 'm',
      stream: false,
      max_tokens: 1024,
      messages: [{ role: 'user', content: 'hi' }],
    })
  })

  it('语音合成只发 model + input，缺省不写 voice/格式/语速', () => {
    expect(buildTestPayload('audio-speech', 'doubaotts/taozi', ' 朗读这段 ', defaults)).toEqual({
      model: 'doubaotts/taozi',
      input: '朗读这段',
    })
  })

  it('语音合成传入 voice/格式/非默认语速时才附加', () => {
    expect(buildTestPayload('audio-speech', 'doubaotts/taozi', 'hi', { voice: ' tianmei ', responseFormat: 'mp3', speed: 1.2 })).toEqual({
      model: 'doubaotts/taozi',
      input: 'hi',
      voice: 'tianmei',
      response_format: 'mp3',
      speed: 1.2,
    })
  })

  it('语音合成从不发送 stream 字段', () => {
    expect(buildTestPayload('audio-speech', 'm', 'hi', defaults)).not.toHaveProperty('stream')
  })
})
