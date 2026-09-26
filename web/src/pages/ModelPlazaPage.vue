<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NEmpty, NIcon, NInput, NPagination, NSelect, NSpin, NTag, useMessage } from 'naive-ui'
import { RefreshOutline } from '@vicons/ionicons5'
import { api, type ModelPlazaModel } from '@/services/api'
import ResponsiveFilterPanel from '@/components/ResponsiveFilterPanel.vue'

const message = useMessage()
const queryClient = useQueryClient()
const query = useQuery({
  queryKey: ['model-plaza'],
  queryFn: api.modelPlaza,
  staleTime: 4 * 60 * 60 * 1000,
  retry: 1,
})

const keyword = ref('')
const platform = ref<string | null>(null)
const page = ref(1)
const pageSize = ref(24)
const refreshing = ref(false)

const platformOptions = computed(() => {
  const values = new Map<string, string>()
  for (const model of query.data.value?.models ?? []) {
    if (model.platform) values.set(model.platform, model.platformName || model.platform)
  }
  return [...values.entries()]
    .sort((a, b) => a[1].localeCompare(b[1], 'zh-CN'))
    .map(([value, label]) => ({ value, label }))
})

const filteredModels = computed(() => {
  const normalized = keyword.value.trim().toLowerCase()
  return (query.data.value?.models ?? []).filter(model => {
    const matchesPlatform = !platform.value || model.platform === platform.value
    const haystack = `${model.id} ${model.displayName} ${model.platformName}`.toLowerCase()
    return matchesPlatform && (!normalized || haystack.includes(normalized))
  })
})

const pageCount = computed(() => Math.max(1, Math.ceil(filteredModels.value.length / pageSize.value)))
const pageModels = computed(() => filteredModels.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
const pageSizeOptions = [
  { label: '24 / 页', value: 24 },
  { label: '48 / 页', value: 48 },
  { label: '96 / 页', value: 96 },
]

watch([keyword, platform, pageSize], () => { page.value = 1 })
watch(pageCount, count => { if (page.value > count) page.value = count })

async function refreshModels() {
  refreshing.value = true
  try {
    const data = await api.refreshModelPlaza()
    queryClient.setQueryData(['model-plaza'], data)
    message.success('模型目录已刷新并写入缓存')
  } catch (error) {
    message.error(error instanceof Error ? error.message : '模型目录刷新失败')
  } finally {
    refreshing.value = false
  }
}

function formatLimit(value: number) {
  if (!value) return '未声明'
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(value % 1_000_000 ? 1 : 0)}M`
  if (value >= 1_000) return `${(value / 1_000).toFixed(value % 1_000 ? 1 : 0)}K`
  return value.toLocaleString('zh-CN')
}

function reasoningLabel(model: ModelPlazaModel) {
  if (!model.supportsReasoning) return '不支持'
  const levels = model.reasoningLevels.length > 0 ? model.reasoningLevels.join(' / ') : '支持'
  return model.reasoningTokenLimit ? `${levels} · ≤${formatLimit(model.reasoningTokenLimit)}` : levels
}

function updatedAt(value: string) {
  if (!value) return '尚未记录更新时间'
  return `更新于 ${new Date(value).toLocaleString('zh-CN', { hour12: false })}`
}
</script>

<template>
  <div class="model-plaza-page">
    <NCard :bordered="false" class="model-plaza-toolbar">
      <div class="model-plaza-toolbar-head">
        <div>
          <div class="page-eyebrow">MODEL CATALOG</div>
          <h2>模型广场</h2>
          <p>仅展示已启用插件提供的模型；上下文、输入输出上限和推理能力优先从 models.dev 补全，目录每 4 小时自动缓存。</p>
        </div>
        <NButton circle :loading="refreshing" secondary type="primary" aria-label="刷新模型目录" title="刷新模型目录" @click="refreshModels">
          <template #icon><NIcon :component="RefreshOutline" /></template>
        </NButton>
      </div>
      <ResponsiveFilterPanel class="model-plaza-responsive-filter-panel" variant="plain" title="筛选模型">
      <div class="model-plaza-filter-row">
        <NInput v-model:value="keyword" clearable placeholder="搜索模型名称或 ID" aria-label="搜索模型名称或 ID" />
        <NSelect v-model:value="platform" clearable filterable :options="platformOptions" placeholder="全部平台" aria-label="按平台筛选" />
        <NSelect v-model:value="pageSize" :options="pageSizeOptions" aria-label="每页模型数量" />
      </div>
      </ResponsiveFilterPanel>
      <div class="model-plaza-meta">
        <span>共 {{ filteredModels.length.toLocaleString('zh-CN') }} 个模型</span>
        <span>{{ updatedAt(query.data.value?.updatedAt ?? '') }}</span>
      </div>
    </NCard>

    <div v-if="query.isPending.value" class="center-spin"><NSpin size="large" /></div>
    <div v-else-if="query.isError.value" class="model-plaza-error">
      <NAlert type="error" title="模型目录暂时不可用">
        {{ query.error.value instanceof Error ? query.error.value.message : '请检查网络后重试。' }}
      </NAlert>
      <NButton secondary @click="query.refetch()">重试</NButton>
    </div>
    <template v-else>
      <NEmpty v-if="filteredModels.length === 0" :description="query.data.value?.models.length ? '没有匹配的模型' : '当前没有已启用插件模型'" class="model-plaza-empty" />
      <div v-else class="model-plaza-grid">
        <NCard v-for="model in pageModels" :key="model.id" :bordered="false" class="model-plaza-card">
          <div class="model-card-top">
            <NTag type="info" size="small" round>{{ model.platformName || model.platform }}</NTag>
            <span class="model-card-id" :title="model.id">{{ model.id }}</span>
          </div>
          <h3 :title="model.displayName">{{ model.displayName }}</h3>
          <div class="model-spec-grid">
            <div><span>上下文</span><strong>{{ formatLimit(model.contextWindow) }}</strong></div>
            <div><span>输入</span><strong>{{ formatLimit(model.inputLimit) }}</strong></div>
            <div><span>输出</span><strong>{{ formatLimit(model.outputLimit) }}</strong></div>
          </div>
          <div class="model-reasoning-row">
            <span>推理能力</span>
            <NTag :type="model.supportsReasoning ? 'success' : 'default'" size="small">{{ reasoningLabel(model) }}</NTag>
          </div>
        </NCard>
      </div>
      <div v-if="filteredModels.length > 0" class="model-plaza-footer">
        <span>显示 {{ (page - 1) * pageSize + 1 }}–{{ Math.min(page * pageSize, filteredModels.length) }} / {{ filteredModels.length }}</span>
        <NPagination v-model:page="page" :page-count="pageCount" :page-size="pageSize" :page-slot="5" />
      </div>
    </template>
  </div>
</template>
