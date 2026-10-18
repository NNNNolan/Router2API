<script setup lang="ts">
import { computed, h } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NEmpty, NIcon, NSpin, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { ArrowForwardOutline, CheckmarkCircleOutline, FlashOutline, PulseOutline, RefreshOutline, SwapHorizontalOutline } from '@vicons/ionicons5'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { BarChart, LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import type { EChartsOption } from 'echarts'
import { api, type PlatformMetric } from '@/services/api'
import { useReducedMotion } from '@/composables/useReducedMotion'

use([CanvasRenderer, BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent])
const reducedMotion = useReducedMotion()
const metricsQuery = useQuery({ queryKey: ['metrics'], queryFn: api.metrics, refetchInterval: 15_000 })
const platformsQuery = useQuery({ queryKey: ['platforms'], queryFn: api.platforms })
const trendQuery = useQuery({
  queryKey: ['dashboard-trend'],
  queryFn: () => {
    const toUtc = new Date()
    const fromUtc = new Date(toUtc)
    fromUtc.setHours(0, 0, 0, 0)
    return api.analytics({ fromUtc, toUtc, bucket: 'hour' })
  },
  refetchInterval: 60_000,
})
const metrics = computed(() => metricsQuery.data.value)
const platforms = computed(() => platformsQuery.data.value ?? [])
const successRate = computed(() => {
  if (!metrics.value) return null
  const completed = metrics.value.successfulRequests + metrics.value.failedRequests
  return completed ? (metrics.value.successfulRequests / completed * 100).toFixed(1) : null
})
const updatedAt = computed(() => metricsQuery.dataUpdatedAt.value
  ? new Date(metricsQuery.dataUpdatedAt.value).toLocaleTimeString('zh-CN', { hour12: false }) : '')
const refreshing = computed(() => metricsQuery.isFetching.value || trendQuery.isFetching.value || platformsQuery.isFetching.value)
const chartOption = computed<EChartsOption>(() => ({
  animation: !reducedMotion.value,
  animationDuration: 260,
  animationDurationUpdate: 180,
  textStyle: { fontFamily: 'Inter, "PingFang SC", "Microsoft YaHei", sans-serif', color: '#667085' },
  tooltip: { trigger: 'axis' },
  legend: { top: 0, right: 0, icon: 'roundRect', itemWidth: 12, itemHeight: 4, textStyle: { color: '#667085', fontSize: 12 } },
  grid: { left: 8, right: 12, top: 42, bottom: 8, containLabel: true },
  xAxis: {
    type: 'category', boundaryGap: true,
    data: trendQuery.data.value?.buckets.map(bucket => new Date(bucket.startUtc).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })) ?? [],
    axisLine: { lineStyle: { color: '#E4E7EC' } }, axisTick: { show: false }, axisLabel: { hideOverlap: true },
  },
  yAxis: { type: 'value', minInterval: 1, splitLine: { lineStyle: { color: '#EEF0F3', type: 'dashed' } } },
  series: [
    { name: '请求数', type: 'line', smooth: false, symbolSize: 5, data: trendQuery.data.value?.buckets.map(bucket => bucket.requests) ?? [], lineStyle: { width: 2, color: '#2563EB' }, itemStyle: { color: '#2563EB' }, areaStyle: { color: 'rgba(37, 99, 235, .05)' } },
    { name: '失败数', type: 'bar', barMaxWidth: 12, data: trendQuery.data.value?.buckets.map(bucket => bucket.failures) ?? [], itemStyle: { color: '#F97066', borderRadius: [3, 3, 0, 0] } },
  ],
}))
const columns: DataTableColumns<PlatformMetric> = [
  { title: '平台', key: 'platform' },
  { title: '请求数', key: 'requests', align: 'right' },
  { title: '失败数', key: 'errors', align: 'right' },
  { title: '平均延迟', key: 'averageLatencyMs', align: 'right', render: row => `${Math.round(row.averageLatencyMs)} ms` },
  { title: '请求状态', key: 'status', align: 'right', render: row => h(NTag, { type: !row.requests ? 'default' : row.errors ? 'warning' : 'success', size: 'small', bordered: false }, { default: () => !row.requests ? '无请求' : row.errors ? '有失败' : '无失败' }) },
]
async function refresh() {
  await Promise.all([metricsQuery.refetch(), trendQuery.refetch(), platformsQuery.refetch()])
}
</script>

<template>
  <div class="section-heading">
    <div><h2>运行概况</h2><p>查看模型调用、请求表现与平台资源。</p></div>
    <div class="page-actions">
      <RouterLink to="/analytics" class="dashboard-link">查看分析<NIcon :component="ArrowForwardOutline" /></RouterLink>
      <NButton secondary :loading="refreshing" @click="refresh"><template #icon><NIcon :component="RefreshOutline" /></template>刷新</NButton>
    </div>
  </div>
  <NAlert v-if="metricsQuery.isError.value" type="error" title="指标暂时不可用" class="mb-20">无法读取服务指标，请重试或检查服务连接。<span v-if="metrics">下方保留上次获取的数据。</span></NAlert>
  <NSpin v-if="metricsQuery.isPending.value" size="large" class="center-spin" />
  <div v-if="metrics" class="metrics-grid">
    <NCard class="metric-card" :bordered="false">
      <div class="metric-label">今日请求<NIcon class="metric-icon" :component="PulseOutline" :size="18" /></div>
      <div class="metric-value">{{ metrics.requestsToday.toLocaleString() }}<small>次</small></div>
      <p class="metric-note">累计 {{ metrics.totalRequests.toLocaleString() }} 次请求</p>
    </NCard>
    <NCard class="metric-card" :bordered="false">
      <div class="metric-label">累计成功率<NIcon class="metric-icon" :component="CheckmarkCircleOutline" :size="18" /></div>
      <div class="metric-value">{{ successRate ?? '—' }}<small v-if="successRate !== null">%</small></div>
      <p class="metric-note">{{ successRate === null ? '暂无已完成的请求' : `成功 ${metrics.successfulRequests.toLocaleString()} / 失败 ${metrics.failedRequests.toLocaleString()}` }}</p>
    </NCard>
    <NCard class="metric-card" :bordered="false">
      <div class="metric-label">平均延迟<NIcon class="metric-icon" :component="FlashOutline" :size="18" /></div>
      <div class="metric-value">{{ Math.round(metrics.averageLatencyMs).toLocaleString() }}<small>ms</small></div>
      <p class="metric-note">全部请求的平均耗时</p>
    </NCard>
    <NCard class="metric-card" :bordered="false">
      <div class="metric-label">进行中请求<NIcon class="metric-icon" :component="SwapHorizontalOutline" :size="18" /></div>
      <div class="metric-value">{{ metrics.activeRequests }}<small>个</small></div>
      <p class="metric-note">最近一分钟 {{ metrics.requestsPerMinute }} 次请求</p>
    </NCard>
  </div>
  <div class="dashboard-grid">
    <NCard title="今日请求趋势" class="dashboard-card" :bordered="false">
      <template #header-extra><span class="card-caption">按小时统计</span></template>
      <NAlert v-if="trendQuery.isError.value" type="error" title="请求趋势加载失败"><NButton text type="primary" @click="trendQuery.refetch()">重新获取</NButton></NAlert>
      <div v-else-if="trendQuery.isPending.value" class="chart-state"><NSpin /></div>
      <NEmpty v-else-if="!trendQuery.data.value?.buckets.length" class="chart-state" description="今天还没有请求记录" />
      <template v-else>
        <div class="chart-wrap"><VChart :option="chartOption" autoresize /></div>
        <p class="sr-only">{{ trendQuery.data.value?.buckets.map(bucket => `${bucket.startUtc}：${bucket.requests} 次请求，${bucket.failures} 次失败`).join('；') }}</p>
      </template>
      <p class="chart-caption">来自已记录的请求日志，每分钟更新。</p>
    </NCard>
    <NCard title="平台资源" class="dashboard-card" :bordered="false">
      <template #header-extra><span v-if="platformsQuery.isSuccess.value" class="card-caption">{{ platforms.length }} 个平台</span></template>
      <NAlert v-if="platformsQuery.isError.value" type="error" title="平台加载失败"><NButton text type="primary" @click="platformsQuery.refetch()">重新获取</NButton></NAlert>
      <div v-else-if="platformsQuery.isPending.value" class="chart-state"><NSpin /></div>
      <div v-else class="platform-list platform-list-scroll">
        <div v-for="platform in platforms" :key="platform.name" class="platform-row">
          <div class="platform-icon">{{ platform.displayName.slice(0, 1) }}</div>
          <div class="platform-info"><strong>{{ platform.displayName }}</strong><span>{{ platform.name }}</span></div>
          <NTag :type="platform.enabled ? 'success' : 'default'" :bordered="false" size="small">{{ platform.enabled ? '已启用' : '已停用' }}</NTag>
        </div>
        <NEmpty v-if="!platforms.length" description="安装插件后，平台将显示在这里"><template #extra><RouterLink to="/plugins" class="dashboard-link">管理插件<NIcon :component="ArrowForwardOutline" /></RouterLink></template></NEmpty>
      </div>
    </NCard>
  </div>
  <NCard v-if="metrics" title="平台请求明细" class="dashboard-card" :bordered="false">
    <template #header-extra><span class="card-caption">累计数据 · {{ updatedAt }} 更新</span></template>
    <NDataTable :columns="columns" :data="metrics.platforms" :bordered="false" :scroll-x="620"><template #empty><NEmpty description="暂无请求数据" /></template></NDataTable>
  </NCard>
</template>
