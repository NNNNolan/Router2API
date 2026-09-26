<script setup lang="ts">
import { computed, h, reactive, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NDatePicker, NEmpty, NIcon, NInput, NPagination, NSelect, NSpin, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type TaskLog } from '@/services/api'
import { RefreshOutline } from '@vicons/ionicons5'
import ResponsiveFilterPanel from '@/components/ResponsiveFilterPanel.vue'

type TimeRange = [number, number]
const defaultTimeRange = (): TimeRange => { const now = new Date(); const start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime(); return [start, now.getTime() + 60 * 60 * 1000] }
const page = ref(1); const pageSize = ref<20 | 50 | 100>(20); const timeRange = ref<TimeRange | null>(defaultTimeRange()); const filter = reactive({ pluginKey: '', taskName: '', status: '', keyword: '' })
const query = useQuery({ queryKey: computed(() => ['task-logs', page.value, pageSize.value, timeRange.value?.[0] ?? null, timeRange.value?.[1] ?? null, { ...filter }]), queryFn: () => api.taskLogs({ page: page.value, pageSize: pageSize.value, fromUtc: timeRange.value ? new Date(timeRange.value[0]) : undefined, toUtc: timeRange.value ? new Date(timeRange.value[1]) : undefined, ...filter }) })
watch(() => [timeRange.value?.[0], timeRange.value?.[1], filter.pluginKey, filter.taskName, filter.status, filter.keyword], () => { page.value = 1 })
function resetTimeRange() { timeRange.value = defaultTimeRange() }
const rows = computed(() => query.data.value?.items ?? [])
const columns: DataTableColumns<TaskLog> = [
  { title: '开始时间', key: 'startedAt', render: row => new Date(row.startedAt).toLocaleString() }, { title: '插件', key: 'pluginKey' }, { title: '任务', key: 'taskName' }, { title: '账号', key: 'accountId', render: row => row.accountId || '—' }, { title: '结果', key: 'message', render: row => row.message || row.error || '—' }, { title: '平台', key: 'platform', render: row => row.platform || '—' }, { title: '耗时', key: 'durationMs', render: row => `${row.durationMs} ms` }, { title: '状态', key: 'status', render: row => h(NTag, { type: row.status === 'Success' ? 'success' : row.status === 'Skipped' ? 'warning' : row.status === 'Cancelled' ? 'default' : 'error', round: true }, { default: () => row.status }) },
]
</script>
<template><div class="table-page"><NCard :bordered="false" class="table-card"><ResponsiveFilterPanel title="筛选任务日志"><div class="table-filter-heading"><div><strong>筛选任务日志</strong><span>默认从今天 00:00:00 查询到当前时间后 1 小时，可按插件、任务或状态筛选</span></div><NButton quaternary size="small" @click="resetTimeRange">重置时间</NButton></div><div class="filter-row"><NDatePicker v-model:value="timeRange" type="datetimerange" clearable format="yyyy-MM-dd HH:mm" start-placeholder="开始时间" end-placeholder="结束时间" /><NInput v-model:value="filter.pluginKey" clearable placeholder="PluginKey" /><NInput v-model:value="filter.taskName" clearable placeholder="任务名称" /><NSelect v-model:value="filter.status" clearable :options="[{ label: '成功', value: 'Success' }, { label: '失败', value: 'Failed' }, { label: '已跳过', value: 'Skipped' }, { label: '取消', value: 'Cancelled' }]" placeholder="状态" /><NInput v-model:value="filter.keyword" clearable placeholder="关键词" /></div></ResponsiveFilterPanel><NSpin v-if="query.isPending.value" /><NAlert v-else-if="query.isError.value" type="error" title="任务日志加载失败">请刷新后重试。</NAlert><div v-else class="table-data-wrap"><NDataTable class="table-data-grid" :columns="columns" :data="rows" :scroll-x="1180" :flex-height="true" :bordered="false"><template #empty><NEmpty description="暂无任务执行记录" /></template></NDataTable></div><div class="table-footer"><span>共 {{ query.data.value?.total ?? 0 }} 条</span><div class="table-pagination"><NButton secondary circle :loading="query.isFetching.value" aria-label="刷新任务日志" title="刷新" @click="query.refetch()"><template #icon><NIcon :component="RefreshOutline" /></template></NButton><NPagination v-model:page="page" v-model:page-size="pageSize" :page-count="query.data.value?.totalPages ?? 1" :page-sizes="[20, 50, 100]" show-size-picker /></div></div></NCard></div></template>
