<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NEmpty, NPagination, NSelect, NSpin, NTabPane, NTabs, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type ObservedRequest, type TraceStage, type TraceTopologyLink } from '@/services/api'
import { endpointLabel, outcomeLabels, stageLabels } from '@/services/observationLabels'
import PageHeader from '@/components/PageHeader.vue'

const route = useRoute()
const router = useRouter()
const range = ref('24h')
const pluginKey = ref<string | null>(null)
const page = ref(1)
const requestId = computed(() => typeof route.params.id === 'string' ? route.params.id : '')
const activeTab = computed(() => route.path.includes('/requests') ? 'requests' : 'topology')
const ranges = [{ label: '最近 24 小时', value: '24h' }, { label: '最近 7 天', value: '7d' }, { label: '最近 30 天', value: '30d' }]
function bounds() {
  const toUtc = new Date()
  return { toUtc, fromUtc: new Date(toUtc.getTime() - (range.value === '7d' ? 7 : range.value === '30d' ? 30 : 1) * 86400000), pluginKey: pluginKey.value ?? undefined }
}
watch([range, pluginKey], () => { page.value = 1 })
const plugins = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })
const pluginOptions = computed(() => plugins.data.value?.map(plugin => ({ label: plugin.name, value: plugin.pluginKey })) ?? [])
const topology = useQuery({ queryKey: computed(() => ['trace-topology', range.value, pluginKey.value]), queryFn: () => api.traceTopology(bounds()), refetchInterval: 30_000 })
const requests = useQuery({ queryKey: computed(() => ['trace-requests', range.value, pluginKey.value, page.value]), queryFn: () => api.traceRequests({ ...bounds(), page: page.value }), refetchInterval: 30_000 })
const detail = useQuery({ queryKey: computed(() => ['trace-request', requestId.value]), queryFn: () => api.traceRequest(requestId.value), enabled: computed(() => Boolean(requestId.value)) })
const stages = computed(() => {
  const depths = new Map<string, number>()
  return [...(detail.data.value?.trace?.stages ?? [])].sort((a, b) => a.startMs - b.startMs).map(stage => {
    const depth = (depths.get(stage.parentId) ?? -1) + 1
    depths.set(stage.id, depth)
    return { ...stage, depth }
  })
})
const topologyPlugins = computed(() => {
  const groups = new Map<string, { pluginKey: string; links: TraceTopologyLink[]; requests: number; attempts: number; failures: number; latencyTotal: number }>()
  for (const link of topology.data.value?.links ?? []) {
    let group = groups.get(link.pluginKey)
    if (!group) {
      group = { pluginKey: link.pluginKey, links: [], requests: 0, attempts: 0, failures: 0, latencyTotal: 0 }
      groups.set(link.pluginKey, group)
    }
    group.links.push(link)
    group.requests += link.requests
    group.attempts += link.attempts
    group.failures += link.failures
    group.latencyTotal += link.averageLatencyMs * link.attempts
  }
  return [...groups.values()].sort((a, b) => b.requests - a.requests || a.pluginKey.localeCompare(b.pluginKey))
})
const topologyTotals = computed(() => (topology.data.value?.clients ?? []).reduce((total, client) => ({
  requests: total.requests + client.requests,
  failures: total.failures + client.failures,
}), { requests: 0, failures: 0 }))
const timelineDuration = computed(() => Math.max(1, detail.data.value?.trace?.durationMs ?? 1))
const ticks = [0, .25, .5, .75, 1]
const ms = (value: number) => `${value.toLocaleString('zh-CN', { maximumFractionDigits: 1 })} ms`
const count = (value: number) => value.toLocaleString('zh-CN')
function outcomeType(outcome: string) {
  if (outcome === 'failed' || outcome === 'blocked') return 'error'
  if (['cancelled', 'cooldown', 'unavailable'].includes(outcome)) return 'warning'
  return 'default'
}
const columns: DataTableColumns<ObservedRequest> = [
  { title: '时间', key: 'createdAt', render: row => new Date(row.createdAt).toLocaleString('zh-CN') },
  { title: '入口', key: 'endpoint', render: row => endpointLabel(row.endpoint) },
  { title: '模型', key: 'model', render: row => row.model ?? '—' },
  { title: '结果', key: 'success', render: row => h(NTag, { size: 'small', type: row.cancelled ? 'warning' : row.success ? 'success' : 'error', bordered: false }, { default: () => row.cancelled ? '取消' : `HTTP ${row.statusCode}` }) },
  { title: '总耗时', key: 'durationMs', render: row => ms(row.durationMs) },
  { title: '追踪', key: 'trace', render: row => h(RouterLink, { to: `/traces/requests/${row.id}`, class: 'dashboard-link' }, { default: () => row.trace ? '查看链路' : '无追踪详情' }) },
]
function changeTab(value: string) { router.push(value === 'requests' ? '/traces/requests' : '/traces') }
function bar(stage: TraceStage) {
  const left = Math.max(0, Math.min(100, stage.startMs / timelineDuration.value * 100))
  return { left: `${left}%`, width: `${Math.min(100 - left, Math.max(.4, stage.durationMs / timelineDuration.value * 100))}%` }
}
function refresh() { requestId.value ? detail.refetch() : activeTab.value === 'topology' ? topology.refetch() : requests.refetch() }
</script>

<template>
  <PageHeader title="链路追踪" description="从请求入口经过宿主到实际插件，查看映射切换、资源选择和尝试耗时。">
    <template #actions><NButton :loading="detail.isFetching.value || topology.isFetching.value || requests.isFetching.value" @click="refresh">刷新</NButton></template>
  </PageHeader>
  <p class="metric-note mb-20">只显示宿主可观测的阶段，不追踪插件内部上游。流式请求在完成或取消后记录最终结果；统计基于已保留日志。</p>
  <NTabs :value="activeTab" type="line" @update:value="changeTab">
    <NTabPane name="topology" tab="流量拓扑">
      <div class="observation-toolbar"><NSelect v-model:value="range" :options="ranges" aria-label="拓扑范围" /><NSelect v-model:value="pluginKey" :options="pluginOptions" clearable placeholder="全部插件" aria-label="筛选插件" /></div>
      <NAlert v-if="topology.isError.value" type="error" title="拓扑加载失败"><NButton text @click="topology.refetch()">重试</NButton></NAlert>
      <NSpin v-else-if="topology.isPending.value" />
      <NCard v-else :bordered="false" class="dashboard-card trace-topology-card">
        <NEmpty v-if="!topology.data.value?.clients.length" description="暂无已完成的追踪请求" />
        <template v-else>
          <div class="trace-topology-heading"><div><h3>请求流向</h3><p class="metric-note">按入口汇入宿主，再分流到实际观测的插件。</p></div><span class="metric-note">每 30 秒更新 · 点击插件筛选</span></div>
          <div class="trace-topology" role="group" aria-label="调用方到宿主再到插件的流量拓扑">
            <section class="trace-column">
              <h4>调用入口 <span>{{ topology.data.value?.clients.length }}</span></h4>
              <div class="trace-nodes trace-client-nodes" :style="{ '--node-count': topology.data.value?.clients.length }">
                <div v-for="client in topology.data.value?.clients" :key="client.endpoint" class="trace-node trace-client">
                  <strong>{{ endpointLabel(client.endpoint) }}</strong><code>{{ client.endpoint }}</code>
                  <div class="trace-node-metrics"><span><b>{{ count(client.requests) }}</b> 请求</span><span :class="{ 'trace-error': client.failures > 0 }">{{ count(client.failures) }} 失败</span></div>
                  <span class="metric-note">平均总耗时 {{ ms(client.averageLatencyMs) }}</span>
                </div>
              </div>
            </section>
            <section class="trace-column">
              <h4>路由宿主</h4>
              <div class="trace-hub">
                <div class="trace-node trace-host">
                  <span class="trace-host-badge">HOST</span><strong>Router2API</strong>
                  <div class="trace-host-count">{{ count(topologyTotals.requests) }}<small>次请求</small></div>
                  <span :class="{ 'trace-error': topologyTotals.failures > 0 }">{{ count(topologyTotals.failures) }} 次最终失败</span>
                  <div class="trace-host-stages"><span>隐私检查</span><span>模型路由</span><span>资源选择</span></div>
                </div>
              </div>
            </section>
            <section class="trace-column">
              <h4>实际插件 <span>{{ topologyPlugins.length }}</span></h4>
              <NEmpty v-if="!topologyPlugins.length" description="请求未到达插件" class="trace-no-plugin" />
              <div v-else class="trace-nodes trace-plugin-nodes" :style="{ '--node-count': topologyPlugins.length }">
                <button v-for="plugin in topologyPlugins" :key="plugin.pluginKey" class="trace-node trace-plugin" :class="{ 'is-selected': pluginKey === plugin.pluginKey }" :aria-label="`筛选插件 ${plugin.pluginKey}`" :aria-pressed="pluginKey === plugin.pluginKey" @click="pluginKey = plugin.pluginKey">
                  <strong>{{ plugin.pluginKey }}<span aria-hidden="true">↗</span></strong>
                  <div class="trace-node-metrics"><span><b>{{ count(plugin.requests) }}</b> 请求</span><span>{{ count(plugin.attempts) }} 尝试</span></div>
                  <span class="metric-note"><span :class="{ 'trace-error': plugin.failures > 0 }">{{ count(plugin.failures) }} 次失败尝试</span> · {{ plugin.attempts ? ms(plugin.latencyTotal / plugin.attempts) : '未调用' }}</span>
                  <div class="trace-plugin-endpoints"><span v-for="link in plugin.links" :key="link.endpoint">{{ endpointLabel(link.endpoint) }} <b>{{ count(link.requests) }}</b></span></div>
                </button>
              </div>
            </section>
          </div>
          <p class="metric-note trace-topology-foot">请求数按入口去重；同一请求切换插件时会计入多个插件。失败尝试不等于请求最终失败。</p>
        </template>
      </NCard>
    </NTabPane>
    <NTabPane name="requests" tab="请求链路">
      <template v-if="requestId">
        <NButton text class="mb-20" @click="router.push('/traces/requests')">← 返回请求列表</NButton>
        <NAlert v-if="detail.isError.value" type="error" title="请求详情加载失败"><NButton text @click="detail.refetch()">重试</NButton></NAlert>
        <NSpin v-else-if="detail.isPending.value" />
        <template v-else-if="detail.data.value">
          <NCard :bordered="false" class="dashboard-card mb-20">
            <div class="trace-detail-summary"><strong>{{ detail.data.value.model ?? '未指定模型' }}</strong><NTag :type="detail.data.value.cancelled ? 'warning' : detail.data.value.success ? 'success' : 'error'">{{ detail.data.value.cancelled ? '取消 · ' : '' }}HTTP {{ detail.data.value.statusCode }}</NTag><span>{{ ms(detail.data.value.durationMs) }}</span></div>
            <p class="metric-note">{{ endpointLabel(detail.data.value.endpoint) }} · {{ new Date(detail.data.value.createdAt).toLocaleString('zh-CN') }}</p>
            <div class="trace-identifiers"><span>请求 ID <code>{{ detail.data.value.id }}</code></span><span>关联 ID <code>{{ detail.data.value.traceId }}</code></span></div>
          </NCard>
          <NEmpty v-if="!detail.data.value.trace" description="无追踪详情：这条日志生成于追踪功能启用前。" />
          <NCard v-else title="请求时间线" :bordered="false" class="dashboard-card">
            <NAlert v-if="detail.data.value.trace.truncated" type="warning" class="mb-20">阶段数量超过上限，部分观测被省略。</NAlert>
            <NEmpty v-if="!stages.length" description="此请求没有记录到阶段数据" />
            <div v-else class="trace-waterfall-scroll" role="region" aria-label="请求阶段时间线，可横向滚动" tabindex="0">
              <div class="trace-waterfall">
                <div class="trace-stage-row trace-axis-row"><span>阶段 / 归属</span><div class="trace-axis"><span v-for="tick in ticks" :key="tick" :style="{ left: `${tick * 100}%` }">{{ ms(timelineDuration * tick) }}</span></div><span>耗时</span><span>结果</span></div>
                <div v-for="stage in stages" :key="stage.id" class="trace-stage-row" :data-depth="stage.depth">
                  <div class="trace-stage-label" :style="{ paddingLeft: `${Math.min(stage.depth, 6) * 14}px` }"><strong>{{ stageLabels[stage.name] ?? stage.name }}</strong><small>{{ [stage.pluginKey, stage.platform && stage.platform !== stage.pluginKey ? stage.platform : null].filter(Boolean).join(' / ') || '宿主' }}<template v-if="stage.model"> · {{ stage.model }}</template></small></div>
                  <div class="trace-stage-track" role="img" :aria-label="`${stageLabels[stage.name] ?? stage.name}，开始 ${ms(stage.startMs)}，耗时 ${ms(stage.durationMs)}，${outcomeLabels[stage.outcome] ?? stage.outcome}`"><i :style="bar(stage)" :data-outcome="stage.outcome" /><span class="trace-stage-start">{{ ms(stage.startMs) }}</span></div>
                  <span class="trace-stage-duration">{{ ms(stage.durationMs) }}</span>
                  <NTag size="small" :bordered="false" :type="outcomeType(stage.outcome)">{{ outcomeLabels[stage.outcome] ?? stage.outcome }}<template v-if="stage.statusCode"> · {{ stage.statusCode }}</template></NTag>
                </div>
              </div>
            </div>
            <p class="metric-note">时间线仅绘制实际观测阶段；未覆盖的空白可能属于等待或响应消费，不填充推测阶段。</p>
          </NCard>
        </template>
      </template>
      <template v-else>
        <div class="observation-toolbar"><NSelect v-model:value="range" :options="ranges" aria-label="请求范围" /><NSelect v-model:value="pluginKey" :options="pluginOptions" clearable placeholder="全部插件" aria-label="筛选插件" /></div>
        <NAlert v-if="requests.isError.value" type="error" title="请求列表加载失败"><NButton text @click="requests.refetch()">重试</NButton></NAlert>
        <template v-else>
          <NDataTable :columns="columns" :data="requests.data.value?.items ?? []" :loading="requests.isPending.value" :bordered="false" :scroll-x="820" />
          <NPagination v-model:page="page" :page-size="20" :item-count="requests.data.value?.total ?? 0" class="mt-16" />
        </template>
      </template>
    </NTabPane>
  </NTabs>
</template>
