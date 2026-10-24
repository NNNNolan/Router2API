<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { NAlert, NButton, NEmpty, NIcon, NInput, NPagination, NSelect, NSpin, useMessage } from 'naive-ui'
import { RefreshOutline } from '@vicons/ionicons5'
import { api, type ModelPlazaModel } from '@/services/api'
import ResponsiveFilterPanel from '@/components/ResponsiveFilterPanel.vue'
import ModelAvatar from '@/components/ModelAvatar.vue'

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
const pageSize = ref(20)
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

function reasoningLevels(model: ModelPlazaModel) {
  const order = ['low', 'medium', 'high', 'xhigh', 'max']
  return [...new Set(model.reasoningLevels)].sort((first, second) => {
    const firstIndex = order.indexOf(first.toLowerCase())
    const secondIndex = order.indexOf(second.toLowerCase())
    return (firstIndex < 0 ? order.length : firstIndex) - (secondIndex < 0 ? order.length : secondIndex)
  })
}

function updatedAt(value: string) {
  if (!value) return '尚未记录更新时间'
  return `更新于 ${new Date(value).toLocaleString('zh-CN', { hour12: false })}`
}
</script>

<template>
  <div class="model-plaza-page">
    <section class="model-plaza-toolbar" aria-label="模型目录筛选">
      <div class="model-plaza-toolbar-head">
        <div>
          <h2>模型广场</h2>
          <p>浏览已启用的模型，比较上下文与推理能力。</p>
        </div>
        <NButton circle :loading="refreshing" secondary type="primary" aria-label="刷新模型目录" title="刷新模型目录" @click="refreshModels">
          <template #icon><NIcon :component="RefreshOutline" /></template>
        </NButton>
      </div>
      <ResponsiveFilterPanel class="model-plaza-responsive-filter-panel" variant="plain" title="筛选模型">
      <div class="model-plaza-filter-row">
        <NInput v-model:value="keyword" clearable placeholder="搜索模型名称或 ID" aria-label="搜索模型名称或 ID" />
        <NSelect v-model:value="platform" clearable filterable :options="platformOptions" placeholder="全部平台" aria-label="按平台筛选" />
      </div>
      </ResponsiveFilterPanel>
      <div class="model-plaza-meta">
        <span>共 {{ filteredModels.length.toLocaleString('zh-CN') }} 个模型</span>
        <span>{{ updatedAt(query.data.value?.updatedAt ?? '') }}</span>
      </div>
    </section>

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
        <article v-for="model in pageModels" :key="model.id" class="model-plaza-card">
          <div class="model-card-header">
            <ModelAvatar :model-id="model.id" :display-name="model.displayName" :platform="model.platform" />
            <span class="model-badge platform-badge" :title="model.platform">{{ model.platformName || model.platform }}</span>
          </div>
          <h3 :title="model.displayName">{{ model.displayName }}</h3>
          <p class="model-card-id" :title="model.id">{{ model.id }}</p>
          <div v-if="model.contextWindow || model.inputLimit || model.outputLimit" class="model-card-badges">
            <span v-if="model.contextWindow" class="model-badge"><strong>{{ formatLimit(model.contextWindow) }}</strong> 上下文</span>
            <span v-if="model.inputLimit" class="model-badge" :title="`最大输入 ${model.inputLimit.toLocaleString()} Token`">输入 {{ formatLimit(model.inputLimit) }}</span>
            <span v-if="model.outputLimit" class="model-badge" :title="`最大输出 ${model.outputLimit.toLocaleString()} Token`">输出 {{ formatLimit(model.outputLimit) }}</span>
          </div>
          <div class="model-reasoning-section" aria-label="推理等级与预算">
            <div v-if="model.supportsReasoning && model.reasoningLevels.length" class="model-reasoning-levels">
              <span v-for="level in reasoningLevels(model)" :key="level" class="reasoning-level">{{ level }}</span>
            </div>
            <span v-else :class="model.supportsReasoning ? 'reasoning-level' : 'reasoning-unavailable'">{{ model.supportsReasoning ? '支持 · 未声明等级' : '推理未声明' }}</span>
            <span v-if="model.reasoningTokenLimit" class="model-reasoning-limit" :title="`推理预算上限 ${model.reasoningTokenLimit.toLocaleString()} Token`">≤ {{ formatLimit(model.reasoningTokenLimit) }}</span>
          </div>
        </article>
      </div>
      <div v-if="filteredModels.length > 0" class="model-plaza-footer">
        <span>显示 {{ (page - 1) * pageSize + 1 }}–{{ Math.min(page * pageSize, filteredModels.length) }} / {{ filteredModels.length }}</span>
        <NPagination v-model:page="page" v-model:page-size="pageSize" :page-count="pageCount" :page-sizes="[20, 40, 80]" :page-slot="5" show-size-picker class="model-plaza-pagination" />
      </div>
    </template>
  </div>
</template>
