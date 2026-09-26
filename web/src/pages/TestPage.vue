<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import DOMPurify from 'dompurify'
import { marked } from 'marked'
import { useMessage, NAlert, NButton, NCard, NCode, NForm, NFormItem, NInput, NSelect, NSpin, NSpace } from 'naive-ui'
import { api, type ModelDescriptor } from '@/services/api'

const message = useMessage()
const model = ref('')
type TestEndpoint = 'chat-completions' | 'responses' | 'anthropic-messages'
const endpoint = ref<TestEndpoint>('chat-completions')
const models = ref<ModelDescriptor[]>([])
const modelsLoading = ref(false)
const modelsError = ref('')
const prompt = ref('用一句话说明这个模型的能力。')
const responseBody = ref<unknown | null>(null)
const responseElapsedMs = ref<number | null>(null)
const testing = ref(false)
const error = ref('')
const endpointOptions = [
  { label: 'Chat Completions', value: 'chat-completions' },
  { label: 'Responses', value: 'responses' },
  { label: 'Anthropic Messages', value: 'anthropic-messages' },
]
const endpointPaths: Record<TestEndpoint, string> = {
  'chat-completions': '/v1/chat/completions',
  responses: '/v1/responses',
  'anthropic-messages': '/v1/messages',
}
const endpointHint = computed(() => endpoint.value === 'responses'
  ? '使用 input 字段发送 Responses 请求。'
  : endpoint.value === 'anthropic-messages'
    ? '使用 messages 与 max_tokens 字段发送 Anthropic Messages 请求。'
    : '使用 messages 字段发送 Chat Completions 请求。')
const modelOptions = computed(() => models.value.map(item => ({
  label: item.displayName && item.displayName !== item.id ? `${item.displayName} · ${item.id}` : item.id,
  value: item.id,
})))

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function textFromContent(value: unknown): string {
  if (typeof value === 'string') return value
  if (!Array.isArray(value)) return asRecord(value)?.text && typeof asRecord(value)?.text === 'string' ? asRecord(value)?.text as string : ''
  return value.map(item => {
    const record = asRecord(item)
    return typeof record?.text === 'string' ? record.text : typeof record?.content === 'string' ? record.content : ''
  }).filter(Boolean).join('\n\n')
}

function extractResponseText(value: unknown): string {
  const body = asRecord(value)
  if (!body) return ''
  const choice = Array.isArray(body.choices) ? asRecord(body.choices[0]) : null
  const message = asRecord(choice?.message)
  const chatText = textFromContent(message?.content ?? choice?.text)
  if (chatText) return chatText
  if (typeof body.output_text === 'string' && body.output_text) return body.output_text
  if (Array.isArray(body.output)) {
    const outputText = body.output.map(item => {
      const record = asRecord(item)
      return textFromContent(record?.content ?? record?.text)
    }).filter(Boolean).join('\n\n')
    if (outputText) return outputText
  }
  return textFromContent(body.content)
}

const rawResult = computed(() => responseBody.value === null ? '' : JSON.stringify(responseBody.value, null, 2))
const responseText = computed(() => extractResponseText(responseBody.value))
const responseMarkdown = computed(() => {
  const fence = String.fromCharCode(96).repeat(3)
  const source = responseText.value || (rawResult.value ? `${fence}json\n${rawResult.value}\n${fence}` : '')
  return source ? DOMPurify.sanitize(marked.parse(source, { gfm: true, breaks: true, async: false })) : ''
})
const responseSummary = computed(() => {
  const body = asRecord(responseBody.value)
  const usage = asRecord(body?.usage)
  return {
    id: typeof body?.id === 'string' ? body.id : '',
    model: typeof body?.model === 'string' ? body.model : model.value,
    tokens: typeof usage?.total_tokens === 'number' ? usage.total_tokens : typeof usage?.totalTokens === 'number' ? usage.totalTokens : null,
  }
})

async function loadModels() {
  modelsLoading.value = true
  modelsError.value = ''
  try {
    const result = await api.models()
    models.value = Array.isArray(result.data) ? result.data.filter(item => item.id) : []
    if (!models.value.some(item => item.id === model.value)) model.value = models.value[0]?.id ?? ''
  } catch (reason) {
    modelsError.value = reason instanceof Error ? reason.message : '模型列表获取失败'
  } finally {
    modelsLoading.value = false
  }
}

async function runTest() {
  if (!model.value) {
    error.value = '请先从宿主模型目录选择一个模型'
    return
  }
  error.value = ''
  responseBody.value = null
  responseElapsedMs.value = null
  testing.value = true
  const startedAt = performance.now()
  try {
    const common = { model: model.value.trim(), stream: false }
    const payload = endpoint.value === 'responses'
      ? { ...common, input: prompt.value.trim() }
      : endpoint.value === 'anthropic-messages'
        ? { ...common, max_tokens: 1024, messages: [{ role: 'user', content: prompt.value.trim() }] }
        : { ...common, messages: [{ role: 'user', content: prompt.value.trim() }] }
    const { key } = await api.rawApiKey()
    if (!key) throw new Error('宿主 API Key 不可用，请先检查 API Key 配置')
    const result = await api.v1Request(endpointPaths[endpoint.value], payload, key)
    responseBody.value = result
    message.success('请求完成')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '请求失败'
  } finally {
    responseElapsedMs.value = Math.max(0, Math.round(performance.now() - startedAt))
    testing.value = false
  }
}

onMounted(() => { void loadModels() })
</script>

<template>
  <div class="test-grid">
    <NCard title="请求参数" :bordered="false">
      <NForm label-placement="top">
        <NFormItem label="请求端点" required>
          <NSelect v-model:value="endpoint" :options="endpointOptions" />
        </NFormItem>
        <p class="field-help endpoint-help">{{ endpointHint }}</p>
        <NFormItem label="模型" required>
          <NSelect v-model:value="model" :options="modelOptions" :loading="modelsLoading" :disabled="modelsLoading || !modelOptions.length" filterable placeholder="从宿主模型目录选择" />
        </NFormItem>
        <p class="field-help">模型列表来自宿主机 <code>/v1/models</code>，不再手动填写模型 ID。</p>
        <NAlert v-if="modelsError" type="error" title="模型列表获取失败" class="model-error">
          <NSpace align="center"><span>{{ modelsError }}</span><NButton size="small" type="primary" secondary @click="loadModels">重新获取</NButton></NSpace>
        </NAlert>
        <NFormItem label="用户消息"><NInput v-model:value="prompt" type="textarea" :autosize="{ minRows: 5, maxRows: 10 }" /></NFormItem>
        <NButton type="primary" :loading="testing" :disabled="modelsLoading || !model" @click="runTest">发送测试请求</NButton>
      </NForm>
    </NCard>
    <NCard title="响应结果" :bordered="false">
      <NSpin v-if="testing" size="small" />
      <NAlert v-else-if="error" type="error" title="请求失败">{{ error }}</NAlert>
      <div v-else-if="rawResult" class="response-panel" aria-live="polite">
        <div class="response-toolbar"><span class="response-state">已完成</span><span v-if="responseSummary.model">{{ responseSummary.model }}</span><span v-if="responseSummary.tokens !== null">{{ responseSummary.tokens }} tokens</span><span v-if="responseElapsedMs !== null">耗时 {{ responseElapsedMs }} ms</span></div>
        <div class="response-markdown" v-html="responseMarkdown" />
        <details class="response-raw"><summary>查看原始 JSON</summary><NCode :code="rawResult" language="json" word-wrap /></details>
      </div>
      <div v-else class="empty-panel">发送一次请求，响应会以 Markdown 形式显示在这里。</div>
    </NCard>
  </div>
  <NCard class="info-card" :bordered="false">
    <NSpace align="center"><span class="live-dot" /><span>测试请求携带宿主 API Key 进入公开 v1 端点；宿主按模型协议转换上游请求，再还原当前端点的响应格式。</span></NSpace>
  </NCard>
</template>
