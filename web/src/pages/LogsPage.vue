<script setup lang="ts">
import { computed, h, reactive, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NDatePicker, NEmpty, NIcon, NInput, NModal, NPagination, NSpin, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type RequestLog } from '@/services/api'
import { RefreshOutline } from '@vicons/ionicons5'
import ResponsiveFilterPanel from '@/components/ResponsiveFilterPanel.vue'

type TimeRange = [number, number]
const defaultTimeRange = (): TimeRange => { const now = new Date(); const start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime(); return [start, now.getTime() + 60 * 60 * 1000] }
const page = ref(1); const pageSize = ref<20 | 50 | 100>(20); const timeRange = ref<TimeRange | null>(defaultTimeRange()); const filter = reactive({ platform: '', model: '', keyword: '' })
const query = useQuery({ queryKey: computed(() => ['logs', page.value, pageSize.value, timeRange.value?.[0] ?? null, timeRange.value?.[1] ?? null, { ...filter }]), queryFn: () => api.logs({ page: page.value, pageSize: pageSize.value, fromUtc: timeRange.value ? new Date(timeRange.value[0]) : undefined, toUtc: timeRange.value ? new Date(timeRange.value[1]) : undefined, ...filter }), refetchInterval: 15_000 })
watch(() => [timeRange.value?.[0], timeRange.value?.[1], filter.platform, filter.model, filter.keyword], () => { page.value = 1 })
function resetTimeRange() { timeRange.value = defaultTimeRange() }
const selectedLog = ref<RequestLog | null>(null)
const detailVisible = ref(false)
const selectedAttempts = computed(() => selectedLog.value?.attemptDetails ?? [])
const selectedAttemptDuration = computed(() => selectedAttempts.value.reduce((total, attempt) => total + attempt.durationMs, 0))
const emptyAttemptHint = computed(() => selectedLog.value?.cancelled
  ? '请求已被调用方取消，未产生代理尝试'
  : '未记录到代理尝试，可能在资源选择阶段就失败了。')
function openDetails(row: RequestLog) { selectedLog.value = row; detailVisible.value = true }
function attemptTagType(attempt: NonNullable<RequestLog['attemptDetails']>[number]) {
  if (attempt.statusCode !== null && attempt.statusCode >= 200 && attempt.statusCode < 300) return 'success'
  return attempt.isTransportFailure ? 'warning' : 'error'
}
function attemptStatus(attempt: NonNullable<RequestLog['attemptDetails']>[number]) { return attempt.statusCode === null ? '未收到 HTTP 响应' : `HTTP ${attempt.statusCode}` }
function sourceLabel(source?: string | null) {
  return ({ plugin: '插件上报', response: '响应解析', estimated: '宿主估算', mixed: '混合来源', unknown: '来源未知' } as Record<string, string>)[source ?? 'unknown'] ?? '来源未知'
}
const tokenCount = (value?: number) => value === undefined ? '—' : value.toLocaleString('zh-CN')
const rows = computed(() => query.data.value?.items ?? [])
const columns: DataTableColumns<RequestLog> = [
  { title: '时间', key: 'createdAt', width: 170, render: row => new Date(row.createdAt).toLocaleString() },
  { title: '平台', key: 'platform', width: 100, ellipsis: { tooltip: true }, render: row => row.platform || '—' },
  { title: '模型', key: 'model', width: 200, ellipsis: { tooltip: true }, render: row => row.model || '—' },
  { title: '状态码', key: 'statusCode', width: 85 },
  { title: '耗时', key: 'durationMs', width: 100, render: row => `${row.durationMs} ms` },
  { title: '输入 Token', key: 'usage.promptTokens', width: 110, align: 'right', render: row => tokenCount(row.usage?.promptTokens) },
  { title: '输出 Token', key: 'usage.completionTokens', width: 110, align: 'right', render: row => tokenCount(row.usage?.completionTokens) },
  { title: '总 Token', key: 'usage.totalTokens', width: 110, align: 'right', render: row => tokenCount(row.usage?.totalTokens) },
  { title: '用量来源', key: 'usageMetadata', width: 150, render: row => h(NTag, { size: 'small', bordered: false, type: row.usageMetadata?.source === 'estimated' || row.usageMetadata?.source === 'mixed' ? 'warning' : 'default' }, { default: () => `${sourceLabel(row.usageMetadata?.source)}${row.usageMetadata?.isPartial ? ' · 部分' : ''}` }) },
  { title: '结果', key: 'success', width: 85, render: row => h(NTag, { type: row.success ? 'success' : row.cancelled ? 'warning' : 'error', round: true }, { default: () => row.success ? '成功' : row.cancelled ? '已取消' : '失败' }) },
  { title: '详情', key: 'details', width: 95, render: row => h(NButton, { size: 'small', secondary: true, onClick: () => openDetails(row) }, { default: () => row.attemptDetails?.length ? `查看 (${row.attemptDetails.length})` : '查看' }) },
]
</script>
<template>
  <div class="table-page">
  <NCard :bordered="false" class="table-card"><ResponsiveFilterPanel title="筛选请求日志"><div class="table-filter-heading"><div><strong>筛选请求日志</strong><span>默认从今天 00:00:00 查询到当前时间后 1 小时，可按平台、模型或 TraceId 定位</span></div><NButton quaternary size="small" @click="resetTimeRange">重置时间</NButton></div><div class="filter-row"><NDatePicker v-model:value="timeRange" type="datetimerange" clearable format="yyyy-MM-dd HH:mm" start-placeholder="开始时间" end-placeholder="结束时间" /><NInput v-model:value="filter.platform" clearable placeholder="平台" /><NInput v-model:value="filter.model" clearable placeholder="模型" /><NInput v-model:value="filter.keyword" clearable placeholder="TraceId / 关键词" /></div></ResponsiveFilterPanel><NSpin v-if="query.isPending.value" /><NAlert v-else-if="query.isError.value" type="error" title="日志加载失败">请刷新后重试。</NAlert><div v-else class="table-data-wrap"><NDataTable class="table-data-grid" :columns="columns" :data="rows" :scroll-x="860" :flex-height="true" :bordered="false"><template #empty><NEmpty description="暂无请求日志" /></template></NDataTable></div><div class="table-footer"><span>共 {{ query.data.value?.total ?? 0 }} 条</span><div class="table-pagination"><NButton secondary circle :loading="query.isFetching.value" aria-label="刷新请求日志" title="刷新" @click="query.refetch()"><template #icon><NIcon :component="RefreshOutline" /></template></NButton><NPagination v-model:page="page" v-model:page-size="pageSize" :page-count="query.data.value?.totalPages ?? 1" :page-sizes="[20, 50, 100]" show-size-picker /></div></div></NCard>
  </div>
  <NModal v-model:show="detailVisible" preset="card" class="request-detail-modal" :title="selectedLog ? `${selectedLog.model || '模型请求'} · 请求详情` : '请求详情'" style="width: min(820px, calc(100vw - 32px)); height: min(640px, calc(100dvh - 32px)); max-height: calc(100dvh - 32px);">
    <div v-if="selectedLog" class="request-detail">
      <div class="request-detail-meta"><span>TraceId <code>{{ selectedLog.traceId }}</code></span><span>宿主 {{ selectedLog.durationMs }} ms · 上游 {{ selectedAttemptDuration }} ms · {{ new Date(selectedLog.createdAt).toLocaleString() }}</span><NTag v-if="selectedLog.cancelled" type="warning" size="small" round>调用方已取消</NTag></div>
      <div class="request-usage-summary">
        <span>输入 <strong>{{ tokenCount(selectedLog.usage?.promptTokens) }}</strong></span>
        <span>输出 <strong>{{ tokenCount(selectedLog.usage?.completionTokens) }}</strong></span>
        <span>合计 <strong>{{ tokenCount(selectedLog.usage?.totalTokens) }}</strong> Token</span>
        <span>{{ sourceLabel(selectedLog.usageMetadata?.source) }}<template v-if="selectedLog.usageMetadata?.isPartial"> · 仅记录已观察部分</template></span>
      </div>
      <p v-if="selectedLog.usageMetadata" class="usage-note">输入：{{ sourceLabel(selectedLog.usageMetadata.inputSource) }} · 输出：{{ sourceLabel(selectedLog.usageMetadata.outputSource) }} · 合计：{{ sourceLabel(selectedLog.usageMetadata.totalSource) }}<template v-if="selectedLog.usageMetadata.algorithm"> · 算法 {{ selectedLog.usageMetadata.algorithm }}</template></p>
      <NAlert v-if="selectedLog.usageMetadata?.warning" type="warning" :title="selectedLog.usageMetadata.warning">用量观察不完整，统计可能仅包含部分消耗。</NAlert>
      <NEmpty v-if="!selectedAttempts.length" :description="emptyAttemptHint" />
      <div v-else class="request-attempt-list" role="list">
        <div class="request-attempt-list-header" aria-hidden="true"><span>尝试</span><span>代理</span><span>状态</span><span>结果</span><span>耗时</span></div>
        <div v-for="(attempt, index) in selectedAttempts" :key="`${attempt.proxyId ?? 'direct'}-${index}`" class="request-attempt-row" role="listitem">
          <strong class="request-attempt-index">#{{ index + 1 }}</strong><code class="request-attempt-proxy">{{ attempt.proxyAddress || 'direct' }}</code><NTag class="request-attempt-status" :type="attemptTagType(attempt)" round>{{ attemptStatus(attempt) }}</NTag><span class="request-attempt-outcome">{{ attempt.outcome }}</span><span class="request-attempt-duration">{{ attempt.durationMs }} ms</span>
          <p v-if="attempt.reason" class="request-attempt-reason">{{ attempt.reason }}</p>
        </div>
      </div>
    </div>
  </NModal>
</template>
