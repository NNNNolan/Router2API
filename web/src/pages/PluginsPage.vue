<script setup lang="ts">
import { computed, h, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useMessage, NButton, NCard, NDataTable, NEmpty, NModal, NSpin, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type PluginDescriptor, type PluginTask } from '@/services/api'

const message = useMessage()
const queryClient = useQueryClient()
const query = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })
const reloading = ref(false)
const togglingPluginKey = ref<string | null>(null)
const rows = computed(() => query.data.value ?? [])
const selectedPlugin = ref<PluginDescriptor | null>(null)
const showTaskModal = ref(false)

function openTaskModal(plugin: PluginDescriptor) {
  selectedPlugin.value = plugin
  showTaskModal.value = true
}

function rowProps(row: PluginDescriptor) {
  return {
    class: 'plugin-table-row',
    tabindex: 0,
    'aria-label': `查看 ${row.name} 的定时任务`,
    onClick: () => openTaskModal(row),
    onKeydown: (event: KeyboardEvent) => {
      if (event.target !== event.currentTarget) return
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault()
        openTaskModal(row)
      }
    },
  }
}

const columns: DataTableColumns<PluginDescriptor> = [
  { title: '插件', key: 'name', render: row => h('span', { class: 'plugin-name-cell' }, row.name) },
  { title: '版本', key: 'version' },
  { title: '状态', key: 'state', render: row => h(NTag, { type: row.state === 'Active' ? 'success' : row.state === 'Failed' ? 'error' : 'warning', round: true }, { default: () => row.state === 'Active' ? '启用' : row.state === 'Disabled' ? '已禁用' : row.state }) },
  { title: 'in-flight', key: 'inFlight' },
  { title: '任务', key: 'tasks', render: row => row.tasks.length ? `${row.tasks.length} 个` : '—' },
  { title: '加载时间', key: 'loadedAt', render: row => row.state === 'Active' && row.loadedAt ? new Date(row.loadedAt).toLocaleString() : '—' },
  {
    title: '操作',
    key: 'actions',
    render: row => h(NButton, {
      size: 'small',
      secondary: true,
      type: row.state === 'Active' ? 'warning' : 'success',
      loading: togglingPluginKey.value === row.pluginKey,
      disabled: row.state === 'Draining' || (togglingPluginKey.value !== null && togglingPluginKey.value !== row.pluginKey),
      onClick: (event: MouseEvent) => {
        event.stopPropagation()
        void setPluginEnabled(row.pluginKey, row.state !== 'Active')
      },
    }, { default: () => row.state === 'Active' ? '禁用' : '启用' }),
  },
]

const taskColumns: DataTableColumns<PluginTask> = [
  { title: '任务名称', key: 'name' },
  { title: 'Cron', key: 'cron', render: row => h('code', { class: 'task-cron' }, row.cron || '—') },
  { title: '说明', key: 'description', render: row => row.description || '—' },
  {
    title: '操作',
    key: 'actions',
    render: row => h(NButton, {
      size: 'small',
      secondary: true,
      onClick: (event: MouseEvent) => {
        event.stopPropagation()
        if (selectedPlugin.value) void runTask(selectedPlugin.value.pluginKey, row.name)
      },
    }, { default: () => '立即执行' }),
  },
]

async function reload() {
  reloading.value = true
  try {
    await api.reloadPlugins()
    await queryClient.invalidateQueries({ queryKey: ['plugins'] })
    message.success('插件注册表已刷新')
  } catch (error) {
    message.error(error instanceof Error ? error.message : '插件刷新失败')
  } finally {
    reloading.value = false
  }
}

async function runTask(pluginKey: string, taskName: string) {
  try { await api.runPluginTask(pluginKey, taskName); message.success('任务已执行，结果可在任务日志查看') } catch (error) { message.error(error instanceof Error ? error.message : '任务执行失败') }
}

async function setPluginEnabled(pluginKey: string, enabled: boolean) {
  togglingPluginKey.value = pluginKey
  try {
    await api.setPluginEnabled(pluginKey, enabled)
    await queryClient.invalidateQueries({ queryKey: ['plugins'] })
    message.success(enabled ? '插件已启用' : '插件已禁用')
  } catch (error) {
    message.error(error instanceof Error ? error.message : enabled ? '插件启用失败' : '插件禁用失败')
  } finally {
    togglingPluginKey.value = null
  }
}
</script>

<template>
  <NCard title="插件列表" :bordered="false" class="table-card">
    <template #header-extra><NButton type="primary" :loading="reloading" @click="reload">重新加载插件</NButton></template>
    <NSpin v-if="query.isPending.value" />
    <NDataTable v-else :columns="columns" :data="rows" :row-props="rowProps" :scroll-x="840" :bordered="false">
      <template #empty><NEmpty description="暂无插件" /></template>
    </NDataTable>
  </NCard>
  <NCard class="info-card" :bordered="false">
    <strong>插件开发提示</strong>
    <p>插件只引用 Router.Contracts；请求尝试由宿主注入 HttpClient，插件只返回业务分类，不接触代理候选、节点凭证或 Redis。</p>
  </NCard>
  <NModal v-model:show="showTaskModal" preset="card" :title="selectedPlugin ? `${selectedPlugin.name} · 定时任务` : '定时任务'" style="width: min(760px, calc(100vw - 32px))">
    <div v-if="selectedPlugin" class="task-modal-meta">
      <span>{{ selectedPlugin.pluginKey }}</span>
      <NTag type="info" round>{{ selectedPlugin.tasks.length }} 个任务</NTag>
    </div>
    <NEmpty v-if="selectedPlugin && !selectedPlugin.tasks.length" description="该插件没有注册定时任务" />
    <NDataTable v-else-if="selectedPlugin" :columns="taskColumns" :data="selectedPlugin.tasks" :bordered="false">
      <template #empty><NEmpty description="暂无定时任务" /></template>
    </NDataTable>
  </NModal>
</template>
