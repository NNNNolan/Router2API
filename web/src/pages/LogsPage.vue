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
function openDetails(row: RequestLog) { selectedLog.value = row; detailVisible.value = true }
function attemptTagType(attempt: NonNullable<RequestLog['attemptDetails']>[number]) {
  if (attempt.statusCode !== null && attempt.statusCode >= 200 && attempt.statusCode < 300) return 'success'
  return attempt.isTransportFailure ? 'warning' : 'error'
}
function attemptStatus(attempt: NonNullable<RequestLog['attemptDetails']>[number]) { return attempt.statusCode === null ? '未收到 HTTP 响应' : `HTTP ${attempt.statusCode}` }
const rows = computed(() => query.data.value?.items ?? [])
const columns: DataTableColumns<RequestLog> = [
  { title: '时间', key: 'createdAt', render: row => new Date(row.createdAt).toLocaleString() }, { title: '平台', key: 'platform', render: row => row.platform || '—' }, { title: '模型', key: 'model', render: row => row.model || '—' }, { title: '状态码', key: 'statusCode' }, { title: '耗时', key: 'durationMs', render: row => `${row.durationMs} ms` }, { title: '结果', key: 'success', render: row => h(NTag, { type: row.success ? 'success' : 'error', round: true }, { default: () => row.success ? '成功' : '失败' }) }, { title: '详情', key: 'details', render: row => h(NButton, { size: 'small', secondary: true, onClick: () => openDetails(row) }, { default: () => row.attemptDetails?.length ? `查看 (${row.attemptDetails.length})` : '查看' }) },
]
</script>
<template>
  <div class="table-page">
  <NCard :bordered="false" class="table-card"><ResponsiveFilterPanel title="筛选请求日志"><div class="table-filter-heading"><div><strong>筛选请求日志</strong><span>默认从今天 00:00:00 查询到当前时间后 1 小时，可按平台、模型或 TraceId 定位</span></div><NButton quaternary size="small" @click="resetTimeRange">重置时间</NButton></div><div class="filter-row"><NDatePicker v-model:value="timeRange" type="datetimerange" clearable format="yyyy-MM-dd HH:mm" start-placeholder="开始时间" end-placeholder="结束时间" /><NInput v-model:value="filter.platform" clearable placeholder="平台" /><NInput v-model:value="filter.model" clearable placeholder="模型" /><NInput v-model:value="filter.keyword" clearable placeholder="TraceId / 关键词" /></div></ResponsiveFilterPanel><NSpin v-if="query.isPending.value" /><NAlert v-else-if="query.isError.value" type="error" title="日志加载失败">请刷新后重试。</NAlert><div v-else class="table-data-wrap"><NDataTable class="table-data-grid" :columns="columns" :data="rows" :scroll-x="860" :flex-height="true" :bordered="false"><template #empty><NEmpty description="暂无请求日志" /></template></NDataTable></div><div class="table-footer"><span>共 {{ query.data.value?.total ?? 0 }} 条</span><div class="table-pagination"><NButton secondary circle :loading="query.isFetching.value" aria-label="刷新请求日志" title="刷新" @click="query.refetch()"><template #icon><NIcon :component="RefreshOutline" /></template></NButton><NPagination v-model:page="page" v-model:page-size="pageSize" :page-count="query.data.value?.totalPages ?? 1" :page-sizes="[20, 50, 100]" show-size-picker /></div></div></NCard>
  </div>
  <NModal v-model:show="detailVisible" preset="card" class="request-detail-modal" :title="selectedLog ? `${selectedLog.model || '模型请求'} · 请求详情` : '请求详情'" style="width: min(820px, calc(100vw - 32px)); height: min(640px, calc(100dvh - 32px)); max-height: calc(100dvh - 32px);">
    <div v-if="selectedLog" class="request-detail">
      <div class="request-detail-meta"><span>TraceId <code>{{ selectedLog.traceId }}</code></span><span>宿主 {{ selectedLog.durationMs }} ms · 上游 {{ selectedAttemptDuration }} ms · {{ new Date(selectedLog.createdAt).toLocaleString() }}</span></div>
      <NEmpty v-if="!selectedAttempts.length" description="未记录到代理尝试，可能在资源选择阶段就失败了。" />
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
