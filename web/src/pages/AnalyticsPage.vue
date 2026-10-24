<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NEmpty, NInput, NSelect, NSpin, useThemeVars } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { BarChart, LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import type { EChartsOption } from 'echarts'
import { api, type UsageBreakdown } from '@/services/api'
import { useReducedMotion } from '@/composables/useReducedMotion'
import PageHeader from '@/components/PageHeader.vue'

use([CanvasRenderer, BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent])
const reducedMotion = useReducedMotion()
const themeVars = useThemeVars()
const range = ref('today')
const platform = ref<string | null>(null)
const model = ref('')
const ranges = [{ label: '今天', value: 'today' }, { label: '昨天', value: 'yesterday' }, { label: '最近 7 天', value: '7d' }, { label: '最近 30 天', value: '30d' }]
function bounds() {
  const end = new Date()
  const start = new Date(end)
  start.setHours(0, 0, 0, 0)
  if (range.value === 'yesterday') { end.setTime(start.getTime()); start.setDate(start.getDate() - 1) }
  else if (range.value === '7d') start.setDate(start.getDate() - 6)
  else if (range.value === '30d') start.setDate(start.getDate() - 29)
  return { fromUtc: start, toUtc: end, platform: platform.value ?? undefined, model: model.value.trim() || undefined }
}
const query = useQuery({ queryKey: computed(() => ['analytics-overview', range.value, platform.value, model.value]), queryFn: () => api.analyticsOverview(bounds()), refetchInterval: 30_000 })
const platformQuery = useQuery({ queryKey: ['platforms'], queryFn: api.platforms })
const platformOptions = computed(() => platformQuery.data.value?.map(item => ({ label: item.displayName, value: item.name })) ?? [])
const report = computed(() => query.data.value?.range)
const number = (value: number | undefined) => value === undefined ? '—' : value.toLocaleString('zh-CN')
const metrics = computed(() => {
  const data = report.value
  if (!data) return []
  return [
    { label: '请求数', value: number(data.requests) },
    { label: '成功率', value: data.requests ? `${(data.successes / data.requests * 100).toFixed(1)}%` : '—' },
    { label: '输入 Token', value: number(data.inputTokens) },
    { label: '输出 Token', value: number(data.outputTokens) },
    { label: '总 Token', value: number(data.tokens) },
    { label: '平均延迟', value: `${Math.round(data.averageLatencyMs).toLocaleString()} ms` },
    { label: 'P95 延迟', value: `${Math.round(data.p95LatencyMs).toLocaleString()} ms` },
    { label: '实时连接', value: number(query.data.value?.live.activeConnections) },
  ]
})
function chart(tokens: boolean): EChartsOption {
  const data = report.value?.buckets ?? []
  return {
  animation: !reducedMotion.value,
  animationDuration: 260,
  animationDurationUpdate: 180,
  textStyle: { fontFamily: 'Inter, "PingFang SC", "Microsoft YaHei", sans-serif', color: '#667085' },
  tooltip: { trigger: 'axis' },
  legend: { top: 0, right: 0, icon: 'roundRect', itemWidth: 12, itemHeight: 4 },
  grid: { left: 12, right: 24, top: 42, bottom: 52, containLabel: true },
  xAxis: {
    type: 'category', boundaryGap: !tokens,
    axisLabel: { hideOverlap: true, margin: 12 },
    axisLine: { lineStyle: { color: '#E4E7EC' } }, axisTick: { show: false },
    data: data.map(item => new Date(item.startUtc).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false })),
  },
  yAxis: { type: 'value', minInterval: 1, splitLine: { lineStyle: { type: 'dashed', color: '#EEF0F3' } } },
  series: tokens
    ? [{ name: '输入 Token', type: 'line', showSymbol: false, data: data.map(item => item.inputTokens ?? null), itemStyle: { color: themeVars.value.primaryColor }, lineStyle: { width: 2 } },
      { name: '输出 Token', type: 'line', showSymbol: false, data: data.map(item => item.outputTokens ?? null), itemStyle: { color: '#8B5CF6' }, lineStyle: { width: 2 } }]
    : [{ name: '请求数', type: 'line', showSymbol: false, data: data.map(item => item.requests), itemStyle: { color: themeVars.value.primaryColor }, lineStyle: { width: 2 } },
      { name: '失败数', type: 'bar', barMaxWidth: 16, data: data.map(item => item.failures), itemStyle: { color: '#F97066', borderRadius: [3, 3, 0, 0] } }],
  }
}
const requestOption = computed(() => chart(false))
const tokenOption = computed(() => chart(true))
const columns: DataTableColumns<UsageBreakdown> = [
  { title: '名称', key: 'name', ellipsis: { tooltip: true }, minWidth: 150 },
  { title: '请求', key: 'requests', align: 'right', render: row => number(row.requests) },
  { title: '输入', key: 'inputTokens', align: 'right', render: row => number(row.inputTokens) },
  { title: '输出', key: 'outputTokens', align: 'right', render: row => number(row.outputTokens) },
  { title: '合计', key: 'tokens', align: 'right', render: row => number(row.tokens) },
]
</script>
<template>
  <PageHeader title="用量分析" description="查看请求表现、Token 消耗与模型使用情况。">
    <template #actions><NSelect v-model:value="range" :options="ranges" style="width: 145px" /></template>
  </PageHeader>
  <div class="analytics-filters mb-20">
    <NSelect v-model:value="platform" :options="platformOptions" :loading="platformQuery.isFetching.value" clearable placeholder="全部平台" aria-label="按平台筛选用量" />
    <NInput v-model:value="model" clearable placeholder="模型完整 ID" aria-label="按模型筛选用量" />
    <NButton secondary :loading="query.isFetching.value" @click="query.refetch()">刷新</NButton>
  </div>
  <NAlert v-if="query.isError.value" type="error" title="分析数据加载失败" class="mb-20">请重试。<span v-if="report">下方保留上次获取的数据。</span></NAlert>
  <NSpin v-if="query.isPending.value" size="large" class="center-spin" />
  <template v-if="report">
    <div class="analytics-summary-grid mb-20">
      <NCard v-for="metric in metrics" :key="metric.label" :bordered="false" class="metric-card"><div class="metric-label">{{ metric.label }}</div><div class="metric-value">{{ metric.value }}</div></NCard>
    </div>
    <p class="usage-note">已记录用量 · {{ number(report.estimatedRequests) }} 次包含宿主估算 · {{ number(report.partialRequests) }} 次记录不完整 · {{ number(report.unknownUsageRequests) }} 次来源未知。估算与部分记录不等于上游实际账单。</p>
    <div class="usage-chart-grid">
      <NCard title="请求趋势" :bordered="false" class="dashboard-card">
        <div v-if="report.buckets.length" class="chart-wrap"><VChart :option="requestOption" autoresize /></div><NEmpty v-else description="所选时间内没有请求" />
        <p class="sr-only">{{ report.buckets.map(item => `${item.startUtc}：${item.requests} 次请求，${item.failures} 次失败`).join('；') }}</p>
      </NCard>
      <NCard title="Token 用量趋势" :bordered="false" class="dashboard-card">
        <div v-if="report.buckets.length" class="chart-wrap"><VChart :option="tokenOption" autoresize /></div><NEmpty v-else description="所选时间内没有用量记录" />
        <p class="sr-only">{{ report.buckets.map(item => `${item.startUtc}：输入 ${number(item.inputTokens)}，输出 ${number(item.outputTokens)} Token`).join('；') }}</p>
      </NCard>
    </div>
    <div class="usage-breakdown-grid">
      <NCard title="平台用量" :bordered="false" class="dashboard-card"><NDataTable :columns="columns" :data="report.platforms ?? []" :bordered="false" :scroll-x="550" :pagination="{ pageSize: 10 }" /></NCard>
      <NCard title="模型用量" :bordered="false" class="dashboard-card"><NDataTable :columns="columns" :data="report.models ?? []" :bordered="false" :scroll-x="550" :pagination="{ pageSize: 10 }" /></NCard>
    </div>
  </template>
</template>
