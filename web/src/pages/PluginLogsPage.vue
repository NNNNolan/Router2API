<script setup lang="ts">
import { computed, h, reactive, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NDatePicker, NEmpty, NIcon, NInput, NModal, NPagination, NSelect, NSpin, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { BugOutline, RefreshOutline } from '@vicons/ionicons5'
import { api, type PluginLog } from '@/services/api'
import ResponsiveFilterPanel from '@/components/ResponsiveFilterPanel.vue'

type TimeRange = [number, number]
const defaultTimeRange = (): TimeRange => {
  const now = new Date()
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  return [start, now.getTime() + 60 * 60 * 1000]
}

const page = ref(1)
const pageSize = ref<20 | 50 | 100>(20)
const timeRange = ref<TimeRange | null>(defaultTimeRange())
const filter = reactive({ platform: '', taskName: '', level: '', eventType: '', keyword: '' })
const query = useQuery({
  queryKey: computed(() => ['plugin-logs', page.value, pageSize.value, timeRange.value?.[0] ?? null, timeRange.value?.[1] ?? null, { ...filter }]),
  queryFn: () => api.pluginLogs({
    page: page.value,
    pageSize: pageSize.value,
    fromUtc: timeRange.value ? new Date(timeRange.value[0]) : undefined,
    toUtc: timeRange.value ? new Date(timeRange.value[1]) : undefined,
    ...filter,
  }),
})

watch(() => [timeRange.value?.[0], timeRange.value?.[1], filter.platform, filter.taskName, filter.level, filter.eventType, filter.keyword], () => { page.value = 1 })
function resetTimeRange() { timeRange.value = defaultTimeRange() }
const rows = computed(() => query.data.value?.items ?? [])
const selectedLog = ref<PluginLog | null>(null)
const detailVisible = ref(false)
const levelOptions = [
  { label: '全部级别', value: '' },
  { label: '信息', value: 'Information' },
  { label: '警告', value: 'Warning' },
  { label: '错误', value: 'Error' },
]

function openDetails(row: PluginLog) {
  selectedLog.value = row
  detailVisible.value = true
}

function levelType(level: string) {
  return level === 'Error' ? 'error' : level === 'Warning' ? 'warning' : 'info'
}

function prettyDetails(value: string | null) {
  if (!value) return '没有附加字段'
  try { return JSON.stringify(JSON.parse(value), null, 2) } catch { return value }
}

const columns: DataTableColumns<PluginLog> = [
  { title: '时间', key: 'createdAt', render: row => new Date(row.createdAt).toLocaleString() },
  { title: '插件 / 平台', key: 'pluginKey', render: row => row.pluginKey + (row.platform ? ' / ' + row.platform : '') },
  { title: '任务名称', key: 'taskName', render: row => row.taskName || '—' },
  { title: '事件', key: 'eventType' },
  { title: '级别', key: 'level', render: row => h(NTag, { type: levelType(row.level), round: true }, { default: () => row.level }) },
  { title: '状态码', key: 'statusCode', render: row => row.statusCode ?? '—' },
  { title: '耗时', key: 'durationMs', render: row => row.durationMs == null ? '—' : String(row.durationMs) + ' ms' },
  { title: '消息', key: 'message', ellipsis: { tooltip: true } },
  { title: '详情', key: 'details', render: row => h(NButton, { size: 'small', secondary: true, onClick: () => openDetails(row) }, { default: () => '查看' }) },
]
</script>

<template>
  <div class="table-page">
    <NCard :bordered="false" class="table-card">
      <ResponsiveFilterPanel title="筛选插件日志">
        <div class="table-filter-heading">
          <div><strong><NIcon :component="BugOutline" /> 筛选插件日志</strong><span>记录每个插件尝试的模型、账号、代理路由、上游状态码和异常；默认查询今天 00:00 到当前时间后 1 小时。</span></div>
          <NButton quaternary size="small" @click="resetTimeRange">重置时间</NButton>
        </div>
        <div class="filter-row">
          <NDatePicker v-model:value="timeRange" type="datetimerange" clearable format="yyyy-MM-dd HH:mm" start-placeholder="开始时间" end-placeholder="结束时间" />
          <NInput v-model:value="filter.platform" clearable placeholder="平台" />
          <NInput v-model:value="filter.taskName" clearable placeholder="任务名称" />
          <NSelect v-model:value="filter.level" clearable :options="levelOptions" placeholder="日志级别" />
          <NInput v-model:value="filter.eventType" clearable placeholder="事件类型" />
          <NInput v-model:value="filter.keyword" clearable placeholder="消息 / TraceId" />
        </div>
      </ResponsiveFilterPanel>
      <NSpin v-if="query.isPending.value" />
      <NAlert v-else-if="query.isError.value" type="error" title="插件日志加载失败">请刷新后重试。</NAlert>
      <div v-else class="table-data-wrap">
        <NDataTable class="table-data-grid" :columns="columns" :data="rows" :scroll-x="1180" :flex-height="true" :bordered="false">
          <template #empty><NEmpty description="暂无插件详细日志" /></template>
        </NDataTable>
      </div>
      <div class="table-footer">
        <span>共 {{ query.data.value?.total ?? 0 }} 条</span>
        <div class="table-pagination">
          <NButton secondary circle :loading="query.isFetching.value" aria-label="刷新插件日志" title="刷新" @click="query.refetch()"><template #icon><NIcon :component="RefreshOutline" /></template></NButton>
          <NPagination v-model:page="page" v-model:page-size="pageSize" :page-count="query.data.value?.totalPages ?? 1" :page-sizes="[20, 50, 100]" show-size-picker />
        </div>
      </div>
    </NCard>
  </div>

  <NModal v-model:show="detailVisible" preset="card" class="plugin-log-detail-modal" title="插件日志详情" style="width: min(760px, calc(100vw - 32px)); height: min(720px, calc(100dvh - 32px)); max-height: calc(100dvh - 32px);">
    <div v-if="selectedLog" class="plugin-log-detail">
      <div class="plugin-log-detail-list">
        <div><span>时间</span><strong>{{ new Date(selectedLog.createdAt).toLocaleString() }}</strong></div>
        <div><span>插件</span><strong>{{ selectedLog.pluginKey }}</strong></div>
        <div><span>平台</span><strong>{{ selectedLog.platform || '—' }}</strong></div>
        <div><span>事件</span><strong>{{ selectedLog.eventType }}</strong></div>
        <div><span>级别</span><NTag :type="levelType(selectedLog.level)" round>{{ selectedLog.level }}</NTag></div>
        <div><span>状态码</span><strong>{{ selectedLog.statusCode ?? '—' }}</strong></div>
        <div><span>耗时</span><strong>{{ selectedLog.durationMs == null ? '—' : selectedLog.durationMs + ' ms' }}</strong></div>
        <div><span>模型</span><strong>{{ selectedLog.model || '—' }}</strong></div>
        <div><span>账号</span><strong>{{ selectedLog.accountId || '—' }}</strong></div>
        <div><span>TraceId</span><code>{{ selectedLog.traceId || '—' }}</code></div>
        <div><span>任务</span><strong>{{ selectedLog.taskName || '—' }}</strong></div>
        <div class="plugin-log-detail-message"><span>消息</span><p>{{ selectedLog.message }}</p></div>
      </div>
      <div class="plugin-log-json">
        <span>附加字段</span>
        <pre>{{ prettyDetails(selectedLog.detailsJson) }}</pre>
      </div>
    </div>
  </NModal>
</template>
