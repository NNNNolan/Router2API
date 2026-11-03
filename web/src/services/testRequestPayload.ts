/** 测试窗口支持的公开端点。语音合成与聊天共用同一套宿主管线，只是请求/响应体不同。 */
export type TestEndpoint = 'chat-completions' | 'responses' | 'anthropic-messages' | 'audio-speech'

/** 语音合成的可选参数；留空即沿用插件/账号默认。 */
export interface SpeechOptions {
  voice: string
  responseFormat: '' | 'mp3' | 'wav' | 'ogg'
  speed: number
}

export const endpointPaths: Record<TestEndpoint, string> = {
  'chat-completions': '/v1/chat/completions',
  responses: '/v1/responses',
  'anthropic-messages': '/v1/messages',
  'audio-speech': '/v1/audio/speech',
}

export const endpointOptions: { label: string; value: TestEndpoint }[] = [
  { label: 'Chat Completions', value: 'chat-completions' },
  { label: 'Responses', value: 'responses' },
  { label: 'Anthropic Messages', value: 'anthropic-messages' },
  { label: '语音合成 Audio Speech', value: 'audio-speech' },
]

/**
 * 按端点构造测试请求体。语音合成不带 stream（宿主拒绝流式 TTS），
 * voice/response_format 为空时不写，交给插件按账号设置决定。
 */
export function buildTestPayload(
  endpoint: TestEndpoint,
  model: string,
  prompt: string,
  speech: SpeechOptions = { voice: '', responseFormat: '', speed: 1 },
): Record<string, unknown> {
  const trimmedModel = model.trim()
  const trimmedPrompt = prompt.trim()
  if (endpoint === 'audio-speech') {
    const payload: Record<string, unknown> = { model: trimmedModel, input: trimmedPrompt }
    if (speech.voice.trim()) payload.voice = speech.voice.trim()
    if (speech.responseFormat) payload.response_format = speech.responseFormat
    if (speech.speed !== 1) payload.speed = speech.speed
    return payload
  }

  const common = { model: trimmedModel, stream: false }
  if (endpoint === 'responses') return { ...common, input: trimmedPrompt }
  if (endpoint === 'anthropic-messages') {
    return { ...common, max_tokens: 1024, messages: [{ role: 'user', content: trimmedPrompt }] }
  }
  return { ...common, messages: [{ role: 'user', content: trimmedPrompt }] }
}
