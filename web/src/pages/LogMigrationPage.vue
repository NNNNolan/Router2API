<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NProgress, NSpin, useMessage } from 'naive-ui'
import { api } from '@/services/api'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const queryClient = useQueryClient()
const starting = ref(false)
const query = useQuery({
  queryKey: ['log-migration'],
  queryFn: api.logMigrationStatus,
  refetchInterval: current => current.state.data?.status === 'running' ? 1000 : false,
})
const state = computed(() => query.data.value)
const running = computed(() => state.value?.status === 'running')
const noLogs = computed(() => state.value?.status === 'idle' && state.value.totalTables === 0)
const disabled = computed(() => starting.value || running.value || state.value?.restartRequired === true || state.value?.status === 'completed')
const phaseLabel: Record<string, string> = {
  idle: '待开始', scanning: '扫描旧日志', copying: '复制日志', verifying: '校验日志',
  deleting: '删除已校验的旧表', completed: '已完成', failed: '失败',
}

watch(() => state.value?.status, status => {
  if (status !== 'completed') return
  for (const key of [['logs'], ['plugin-logs'], ['task-logs'], ['analytics-overview'], ['analytics'], ['metrics'], ['privacy-summary'], ['privacy-events'], ['trace-topology'], ['trace-requests'], ['trace-request']]) {
    void queryClient.invalidateQueries({ queryKey: key })
  }
})

async function start() {
  if (disabled.value) return
  starting.value = true
  try {
    queryClient.setQueryData(['log-migration'], await api.logMigrationStart())
  } catch (error) {
    message.error(error instanceof Error ? error.message : '启动日志迁移失败')
    await query.refetch()
  } finally {
    starting.value = false
  }
}
</script>

<template>
  <div class="log-migration-page">
    <PageHeader title="日志迁移" description="将旧业务数据库中的日志迁移到当前日志数据库。" />
    <NSpin v-if="query.isPending.value" class="center-spin" />
    <NAlert v-else-if="query.isError.value" type="error" title="迁移状态读取失败">{{ query.error.value instanceof Error ? query.error.value.message : '请刷新页面后重试。' }}</NAlert>
    <template v-else-if="state">
      <NAlert type="warning" :bordered="false" class="migration-warning">
        迁移前，只能在对应日志与统计页面看到新日志数据库中的内容；迁移期间，历史数据会逐步出现在对应页面。迁移会在校验通过后删除旧业务日志表。删除表不会立即缩小 SQLite 文件。
      </NAlert>
      <NCard title="数据库路径" :bordered="false" class="settings-card migration-card">
        <dl class="migration-paths">
          <div><dt>当前业务数据库</dt><dd>{{ state.sourcePath || '—' }}</dd></div>
          <div><dt>日志数据库</dt><dd>{{ state.targetPath || '—' }}</dd></div>
        </dl>
        <p v-if="noLogs" class="migration-empty">无需迁移</p>
        <template v-else>
          <div class="migration-summary">
            <span>待迁移表 <strong>{{ state.totalTables - state.deletedTables }}</strong></span>
            <span>待迁移行 <strong>{{ Math.max(0, state.totalRows - state.verifiedRows) }}</strong></span>
          </div>
          <div v-if="state.status !== 'idle'" class="migration-progress">
            <div class="migration-progress-heading"><strong>{{ phaseLabel[state.phase] ?? state.phase }}</strong><span>{{ state.progressPercent }}%</span></div>
            <NProgress type="line" :percentage="state.progressPercent" :show-indicator="false" />
            <p>当前表：{{ state.currentTable || '—' }}</p>
            <p>已复制 {{ state.copiedRows.toLocaleString('zh-CN') }} / {{ state.totalRows.toLocaleString('zh-CN') }} 行，已校验 {{ state.verifiedRows.toLocaleString('zh-CN') }} 行；已删除 {{ state.deletedTables }} / {{ state.totalTables }} 张旧表。</p>
          </div>
          <NAlert v-if="state.error" type="error" title="迁移失败" class="migration-error">{{ state.error }}</NAlert>
        </template>
        <NAlert v-if="state.restartRequired" type="warning" title="需要重启宿主">日志数据库路径已变更，请重启后再开始迁移。</NAlert>
        <div class="migration-actions">
          <NButton type="primary" :loading="starting || running" :disabled="disabled || noLogs" @click="start">{{ state.status === 'failed' ? '重试迁移' : state.status === 'completed' ? '已完成' : '开始迁移' }}</NButton>
        </div>
      </NCard>
    </template>
  </div>
</template>

<style scoped>
.log-migration-page { display: grid; gap: 16px; }
.migration-warning { line-height: 1.7; }
.migration-card { display: grid; gap: 18px; }
.migration-paths { display: grid; gap: 12px; margin: 0; }
.migration-paths div { display: grid; grid-template-columns: minmax(150px, 190px) minmax(0, 1fr); gap: 12px; }
.migration-paths dt { color: var(--muted); }
.migration-paths dd { margin: 0; overflow-wrap: anywhere; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
.migration-summary { display: flex; flex-wrap: wrap; gap: 28px; }
.migration-summary strong { margin-left: 6px; font-variant-numeric: tabular-nums; }
.migration-empty { color: var(--text-color-2); }
.migration-progress { display: grid; gap: 8px; }
.migration-progress-heading { display: flex; justify-content: space-between; }
.migration-progress p { margin: 0; color: var(--text-color-2); font-size: 13px; }
.migration-error { margin-top: 4px; }
.migration-actions { display: flex; justify-content: flex-end; }
@media (max-width: 600px) { .migration-paths div { grid-template-columns: 1fr; gap: 4px; } }
</style>
