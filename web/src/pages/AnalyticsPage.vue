<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NCard, NSelect, NSpin, NStatistic } from 'naive-ui'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { BarChart, LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import type { EChartsOption } from 'echarts'
import { api } from '@/services/api'

use([CanvasRenderer, BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent])
const range = ref('today')
const ranges = [{ label: '今天', value: 'today' }, { label: '昨天', value: 'yesterday' }, { label: '最近 7 天', value: '7d' }, { label: '最近 30 天', value: '30d' }]
const bounds = computed(() => { const now = new Date(); const end = new Date(now); const start = new Date(now); start.setHours(0, 0, 0, 0); if (range.value === 'yesterday') { start.setDate(start.getDate() - 1); end.setTime(start.getTime()); end.setDate(end.getDate() + 1) } else if (range.value === '7d') start.setDate(start.getDate() - 6); else if (range.value === '30d') start.setDate(start.getDate() - 29); return { fromUtc: start, toUtc: end } })
const query = useQuery({ queryKey: computed(() => ['analytics-overview', range.value, bounds.value.fromUtc.toISOString(), bounds.value.toUtc.toISOString()]), queryFn: () => api.analyticsOverview(bounds.value), refetchInterval: 30_000 })
const report = computed(() => query.data.value?.range)
const liveConnections = computed(() => Number((query.data.value?.live as { activeConnections?: number } | undefined)?.activeConnections ?? 0))
const option = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'axis', axisPointer: { type: 'line' } },
  legend: { top: 0, right: 0, data: ['请求数', '失败数'] },
  grid: { left: 12, right: 24, top: 42, bottom: 52, containLabel: true },
  xAxis: {
    type: 'category',
    axisLabel: { hideOverlap: true, margin: 12, formatter: (value: string) => value.replace(' ', '\n') },
    data: report.value?.buckets.map(item => {
      const date = new Date(item.startUtc)
      const month = String(date.getMonth() + 1).padStart(2, '0')
      const day = String(date.getDate()).padStart(2, '0')
      const hour = String(date.getHours()).padStart(2, '0')
      const minute = String(date.getMinutes()).padStart(2, '0')
      return `${month}-${day} ${hour}:${minute}`
    }) ?? [],
  },
  yAxis: { type: 'value', minInterval: 1 },
  series: [{ name: '请求数', type: 'line', smooth: true, data: report.value?.buckets.map(item => item.requests) ?? [], itemStyle: { color: '#2563EB' }, areaStyle: { color: '#DBEAFE' } }, { name: '失败数', type: 'bar', data: report.value?.buckets.map(item => item.failures) ?? [], itemStyle: { color: '#FCA5A5' } }],
}))
</script>
<template><NAlert v-if="query.isError.value" type="error" title="分析数据加载失败" class="mb-20">请稍后重试。</NAlert><NSpin v-if="query.isPending.value" size="large" class="center-spin" /><template v-else><div class="analytics-summary-grid mb-20"><NCard><NStatistic label="范围请求" :value="report?.requests ?? 0" /></NCard><NCard><NStatistic label="失败请求" :value="report?.failures ?? 0" /></NCard><NCard><NStatistic label="平均延迟" :value="`${Math.round(report?.averageLatencyMs ?? 0)} ms`" /></NCard><NCard><NStatistic label="P95 延迟" :value="`${Math.round(report?.p95LatencyMs ?? 0)} ms`" /></NCard><NCard><NStatistic label="实时连接" :value="liveConnections" /></NCard></div><NCard title="请求趋势" class="dashboard-card"><template #header-extra><NSelect v-model:value="range" :options="ranges" style="width: 150px" /></template><div class="chart-wrap large"><VChart :option="option" autoresize /></div><p class="sr-only">请求趋势数据：{{ report?.buckets.map(item => `${item.startUtc} ${item.requests} 次请求，${item.failures} 次失败`).join('；') }}</p></NCard></template></template>
