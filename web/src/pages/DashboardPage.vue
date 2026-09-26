<script setup lang="ts">
import { computed, h } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NCard, NDataTable, NEmpty, NIcon, NSpin, NStatistic, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { CheckmarkCircleOutline, FlashOutline, PulseOutline, WarningOutline } from '@vicons/ionicons5'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import type { EChartsOption } from 'echarts'
import { api, type Metrics, type PlatformMetric } from '@/services/api'

use([CanvasRenderer, LineChart, GridComponent, TooltipComponent])

const metricsQuery = useQuery({
  queryKey: ['metrics'],
  queryFn: api.metrics,
  refetchInterval: 15_000,
})
const platformsQuery = useQuery({
  queryKey: ['platforms'],
  queryFn: api.platforms,
})

const fallbackMetrics: Metrics = {
  totalRequests: 0,
  successfulRequests: 0,
  failedRequests: 0,
  averageLatencyMs: 0,
  requestsPerMinute: 0,
  requestsToday: 0,
  activeRequests: 0,
  platforms: [],
}
const metrics = computed(() => metricsQuery.data.value ?? fallbackMetrics)
const platforms = computed(() => platformsQuery.data.value ?? [])
const chartOption = computed<EChartsOption>(() => ({
  tooltip: { trigger: 'axis' },
  grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
  xAxis: { type: 'category', boundaryGap: false, data: ['-5m', '-4m', '-3m', '-2m', '-1m', '现在'] },
  yAxis: { type: 'value', minInterval: 1 },
  series: [{
    type: 'line',
    smooth: true,
    symbol: 'none',
    data: [0, 0, 0, 0, metrics.value.requestsPerMinute, metrics.value.totalRequests],
    lineStyle: { color: '#2563EB', width: 3 },
    areaStyle: { color: 'rgba(37, 99, 235, 0.10)' },
  }],
}))

const columns: DataTableColumns<PlatformMetric> = [
  { title: '平台', key: 'platform' },
  { title: '请求数', key: 'requests' },
  { title: '错误数', key: 'errors' },
  { title: '平均延迟', key: 'averageLatencyMs', render: row => `${Math.round(row.averageLatencyMs)} ms` },
  {
    title: '状态',
    key: 'status',
    render: row => row.errors === 0
      ? h(NTag, { type: 'success', round: true }, { default: () => '稳定' })
      : h(NTag, { type: 'warning', round: true }, { default: () => '需关注' }),
  },
]
</script>

<template>
  <NAlert v-if="metricsQuery.isError.value" type="error" title="指标暂时不可用" class="mb-20">
    无法读取服务指标，请确认后端已启动并且当前会话仍然有效。
  </NAlert>
  <NSpin v-if="metricsQuery.isPending.value" size="large" class="center-spin" />
  <template v-else>
    <div class="metrics-grid">
      <NCard class="metric-card">
        <NStatistic label="累计请求" :value="metrics.totalRequests">
          <template #prefix><NIcon :component="PulseOutline" color="#2563EB" /></template>
          <template #suffix><span class="stat-suffix">次</span></template>
        </NStatistic>
      </NCard>
      <NCard class="metric-card">
        <NStatistic label="成功请求" :value="metrics.successfulRequests">
          <template #prefix><NIcon :component="CheckmarkCircleOutline" color="#16A34A" /></template>
          <template #suffix><span class="stat-suffix">次</span></template>
        </NStatistic>
      </NCard>
      <NCard class="metric-card">
        <NStatistic label="平均延迟" :value="Math.round(metrics.averageLatencyMs)">
          <template #prefix><NIcon :component="FlashOutline" color="#D97706" /></template>
          <template #suffix><span class="stat-suffix">ms</span></template>
        </NStatistic>
      </NCard>
      <NCard class="metric-card">
        <NStatistic label="失败请求" :value="metrics.failedRequests">
          <template #prefix><NIcon :component="WarningOutline" color="#DC2626" /></template>
          <template #suffix><span class="stat-suffix">次</span></template>
        </NStatistic>
      </NCard>
    </div>

    <div class="dashboard-grid">
      <NCard title="请求趋势" class="dashboard-card">
        <div class="chart-wrap"><VChart :option="chartOption" autoresize /></div>
      </NCard>
      <NCard title="平台状态" class="dashboard-card">
        <div class="platform-list">
          <div v-for="platform in platforms" :key="platform.name" class="platform-row">
            <div class="platform-icon">{{ platform.displayName.slice(0, 1) }}</div>
            <div class="platform-info"><strong>{{ platform.displayName }}</strong><span>{{ platform.name }}</span></div>
            <NTag type="success" round size="small">已启用</NTag>
          </div>
          <NEmpty v-if="!platforms.length" description="暂无平台" />
        </div>
      </NCard>
    </div>

    <NCard title="平台请求明细" class="dashboard-card">
      <NDataTable :columns="columns" :data="metrics.platforms" :bordered="false" :scroll-x="620">
        <template #empty><NEmpty description="暂无请求数据" /></template>
      </NDataTable>
    </NCard>
  </template>
</template>
