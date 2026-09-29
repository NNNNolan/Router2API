<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useMessage, NAlert, NButton, NCard, NForm, NFormItem, NInput, NSelect, NSpin, NSpace } from 'naive-ui'
import { api, type ModelDescriptor } from '@/services/api'
import { formatJsonResponse } from '@/services/formatJsonResponse'

const message = useMessage()
const model = ref('')
type TestEndpoint = 'chat-completions' | 'responses' | 'anthropic-messages'
const endpoint = ref<TestEndpoint>('chat-completions')
const models = ref<ModelDescriptor[]>([])
const modelsLoading = ref(false)
const modelsError = ref('')
const prompt = ref('用一句话说明这个模型的能力。')
const responseBody = ref<string | null>(null)
const formattedResponse = computed(() => responseBody.value === null ? '' : formatJsonResponse(responseBody.value))
const responseStatus = ref<number | null>(null)
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
  responseStatus.value = null
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
    responseBody.value = result.body
    responseStatus.value = result.status
    if (result.status >= 200 && result.status < 300) message.success('请求完成')
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
      <div v-else-if="responseBody !== null" class="response-panel" aria-live="polite">
        <div class="response-toolbar"><span>HTTP {{ responseStatus }}</span><span v-if="responseElapsedMs !== null">耗时 {{ responseElapsedMs }} ms</span></div>
        <pre class="response-text" v-text="formattedResponse" />
      </div>
      <div v-else class="empty-panel">发送一次请求，这里将显示完整响应正文：JSON 自动缩进并显示中文，其他内容保持原样。</div>
    </NCard>
  </div>
  <NCard class="info-card" :bordered="false">
    <NSpace align="center"><span class="live-dot" /><span>测试请求携带宿主 API Key 进入公开 v1 端点；宿主按模型协议转换上游请求，再还原当前端点的响应格式。</span></NSpace>
  </NCard>
</template>
