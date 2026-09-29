<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useMessage, NAlert, NButton, NCard, NCheckbox, NDataTable, NEmpty, NInput, NModal, NPopconfirm, NSelect, NSpin, NTabPane, NTabs, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type PluginDescriptor, type PluginReleaseIndex, type PluginReleaseSummary, type PluginTask } from '@/services/api'

const message = useMessage()
const queryClient = useQueryClient()
const tab = ref('installed')
const pluginsQuery = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })
const repositoriesQuery = useQuery({ queryKey: ['plugin-repositories'], queryFn: api.pluginRepositories })
const installationsQuery = useQuery({ queryKey: ['plugin-installations'], queryFn: api.pluginInstallations })
const updatesQuery = useQuery({ queryKey: ['plugin-updates'], queryFn: api.pluginUpdates, enabled: computed(() => tab.value === 'updates'), staleTime: 300_000 })
const plugins = computed(() => pluginsQuery.data.value ?? [])
const installations = computed(() => installationsQuery.data.value ?? [])
const updates = computed(() => updatesQuery.data.value ?? [])
const repositoryOptions = computed(() => (repositoriesQuery.data.value ?? []).map(item => ({ label: `${item.owner}/${item.repo}`, value: `${item.owner}/${item.repo}` })))
const releaseOptions = computed(() => releases.value.map(item => ({ label: item.tag, value: item.tag })))
const selectedTaskPlugin = ref<PluginDescriptor | null>(null)
const taskModalOpen = ref(false)
const subscribeModalOpen = ref(false)
const repositoryInput = ref('')
const selectedRepository = ref<string | null>(null)
const selectedTag = ref<string | null>(null)
const selectedPluginIds = ref<string[]>([])
const selectedUpdateIds = ref<string[]>([])
const releases = ref<PluginReleaseSummary[]>([])
const releaseIndex = ref<PluginReleaseIndex | null>(null)
const loadingReleases = ref(false)
const loadingIndex = ref(false)
const savingRepository = ref(false)
const installing = ref(false)
const updating = ref(false)
const reloading = ref(false)
const uploadModalOpen = ref(false)
const uploadFile = ref<File | null>(null)
const uploadInput = ref<HTMLInputElement | null>(null)
const uploading = ref(false)
const uploadError = ref('')
const togglingKey = ref<string | null>(null)
const deletingKey = ref<string | null>(null)
const modalError = ref('')
let releaseRequest = 0
let indexRequest = 0

watch(repositoryOptions, options => {
  if (subscribeModalOpen.value && !selectedRepository.value && options.length)
    selectedRepository.value = options[0].value
})

function splitRepository(value: string): [string, string] | null {
  const normalized = value.trim().replace(/^https:\/\/github\.com\//i, '').replace(/\/$/, '').replace(/\.git$/i, '')
  const parts = normalized.split('/')
  return parts.length === 2 && /^[A-Za-z0-9][A-Za-z0-9-]{0,38}$/.test(parts[0]) && /^[A-Za-z0-9_.-]{1,100}$/.test(parts[1])
    ? [parts[0], parts[1]] : null
}

watch(selectedRepository, async value => {
  const ticket = ++releaseRequest
  ++indexRequest
  selectedTag.value = null
  releaseIndex.value = null
  selectedPluginIds.value = []
  releases.value = []
  loadingReleases.value = false
  loadingIndex.value = false
  modalError.value = ''
  if (!value) return
  const parts = splitRepository(value)
  if (!parts) return
  loadingReleases.value = true
  try {
    const result = await api.pluginReleases(...parts)
    if (ticket === releaseRequest) { releases.value = result; selectedTag.value = result[0]?.tag ?? null }
  } catch (error) {
    if (ticket === releaseRequest) modalError.value = error instanceof Error ? error.message : '读取仓库版本失败'
  } finally { if (ticket === releaseRequest) loadingReleases.value = false }
})

watch(selectedTag, async value => {
  const ticket = ++indexRequest
  releaseIndex.value = null
  selectedPluginIds.value = []
  loadingIndex.value = false
  if (!value || !selectedRepository.value) return
  const parts = splitRepository(selectedRepository.value)
  if (!parts) return
  loadingIndex.value = true
  modalError.value = ''
  try {
    const result = await api.pluginReleaseIndex(...parts, value)
    if (ticket === indexRequest) releaseIndex.value = result
  } catch (error) {
    if (ticket === indexRequest) modalError.value = error instanceof Error ? error.message : '读取插件索引失败'
  } finally { if (ticket === indexRequest) loadingIndex.value = false }
})

function setSelected(id: string, checked: boolean, target: 'install' | 'update') {
  const list = target === 'install' ? selectedPluginIds : selectedUpdateIds
  list.value = checked ? [...list.value, id] : list.value.filter(item => item !== id)
}
function installedSource(id: string) { return installations.value.find(item => item.pluginId === id) }
function updateFor(id: string) { return updates.value.find(item => item.installed.pluginId === id) }
async function refreshLocal() {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: ['plugins'] }),
    queryClient.invalidateQueries({ queryKey: ['plugin-installations'] }),
    queryClient.invalidateQueries({ queryKey: ['plugin-updates'] }),
  ])
}
async function reload() {
  reloading.value = true
  try { await api.reloadPlugins(); await refreshLocal(); message.success('插件已重新加载') }
  catch (error) { message.error(error instanceof Error ? error.message : '插件重新加载失败') }
  finally { reloading.value = false }
}
function selectUpload(event: Event) {
  uploadFile.value = (event.target as HTMLInputElement).files?.[0] ?? null
  uploadError.value = ''
}
async function uploadPlugin() {
  const file = uploadFile.value
  if (!file || !file.name.toLowerCase().endsWith('.zip') || file.size <= 0 || file.size > 100 * 1024 * 1024) {
    uploadError.value = '请选择非空 ZIP 文件，最大 100 MiB'
    return
  }
  uploading.value = true
  uploadError.value = ''
  try {
    const plugin = await api.uploadPlugin(file)
    if (plugin.state !== 'Active') throw new Error('插件未成功加载，请检查插件状态和日志')
    message.success(`${plugin.name} 已安装并重新加载`)
    uploadModalOpen.value = false
    uploadFile.value = null
    if (uploadInput.value) uploadInput.value.value = ''
  } catch (error) { uploadError.value = error instanceof Error ? error.message : '上传插件失败' }
  finally {
    try { await refreshLocal() }
    finally { uploading.value = false }
  }
}
async function togglePlugin(plugin: PluginDescriptor) {
  togglingKey.value = plugin.pluginKey
  try {
    await api.setPluginEnabled(plugin.pluginKey, plugin.state !== 'Active')
    await refreshLocal()
    message.success(plugin.state === 'Active' ? '插件已禁用' : '插件已启用')
  } catch (error) { message.error(error instanceof Error ? error.message : '切换插件状态失败') }
  finally { togglingKey.value = null }
}
async function deletePlugin(id: string) {
  deletingKey.value = id
  try { await api.deletePlugin(id); await refreshLocal(); message.success('插件已删除') }
  catch (error) { message.error(error instanceof Error ? error.message : '删除插件失败') }
  finally { deletingKey.value = null }
}
async function addRepository() {
  const parts = splitRepository(repositoryInput.value)
  if (!parts) { modalError.value = '请输入 GitHub 仓库地址或 owner/repo'; return }
  savingRepository.value = true
  modalError.value = ''
  try {
    const saved = await api.addPluginRepository(...parts)
    await queryClient.invalidateQueries({ queryKey: ['plugin-repositories'] })
    selectedRepository.value = `${saved.owner}/${saved.repo}`
    repositoryInput.value = ''
  } catch (error) { modalError.value = error instanceof Error ? error.message : '添加仓库失败' }
  finally { savingRepository.value = false }
}
async function installSelected() {
  const parts = splitRepository(selectedRepository.value ?? '')
  if (!parts || !selectedTag.value || !selectedPluginIds.value.length) return
  installing.value = true
  modalError.value = ''
  try {
    await api.installPlugins(...parts, selectedTag.value, selectedPluginIds.value)
    message.success(`已安装 ${selectedPluginIds.value.length} 个插件`)
    subscribeModalOpen.value = false
  } catch (error) { modalError.value = error instanceof Error ? error.message : '安装插件失败' }
  finally { await refreshLocal(); installing.value = false }
}
async function updateSelected() {
  const chosen = updates.value.filter(item => selectedUpdateIds.value.includes(item.installed.pluginId))
  if (!chosen.length) return
  updating.value = true
  try {
    for (const item of chosen) await api.installPlugins(item.installed.owner, item.installed.repo, item.tag, [item.installed.pluginId])
    selectedUpdateIds.value = []
    message.success(`已更新 ${chosen.length} 个插件`)
  } catch (error) { message.error(error instanceof Error ? error.message : '更新插件失败') }
  finally { await refreshLocal(); updating.value = false }
}
async function runTask(pluginKey: string, taskName: string) {
  try { await api.runPluginTask(pluginKey, taskName); message.success('任务已执行，结果可在任务日志查看') }
  catch (error) { message.error(error instanceof Error ? error.message : '任务执行失败') }
}
const taskColumns: DataTableColumns<PluginTask> = [
  { title: '任务名称', key: 'name' },
  { title: 'Cron', key: 'cron', render: row => h('code', { class: 'task-cron' }, row.cron || '—') },
  { title: '说明', key: 'description', render: row => row.description || '—' },
  { title: '操作', key: 'actions', render: row => h(NButton, { size: 'small', secondary: true, onClick: () => { if (selectedTaskPlugin.value) void runTask(selectedTaskPlugin.value.pluginKey, row.name) } }, { default: () => '立即执行' }) },
]
function openTasks(plugin: PluginDescriptor) { selectedTaskPlugin.value = plugin; taskModalOpen.value = true }
function openSubscribe() {
  modalError.value = ''
  subscribeModalOpen.value = true
  if (!selectedRepository.value && repositoryOptions.value.length) selectedRepository.value = repositoryOptions.value[0].value
}
</script>

<template>
  <div class="plugin-manager">
    <NCard :bordered="false" class="plugin-manager-head"><div class="plugin-manager-headline">
      <div><span class="page-eyebrow">PLUGIN LIBRARY</span><h2>插件管理</h2><p>查看运行状态，按需安装和更新仓库中的插件。</p></div>
      <div class="plugin-manager-actions"><NButton secondary :loading="reloading" :disabled="uploading" @click="reload">重新加载</NButton><NButton secondary :disabled="uploading" @click="uploadError = ''; uploadModalOpen = true">上传插件 ZIP</NButton><NButton type="primary" @click="openSubscribe">添加订阅仓库</NButton></div>
    </div></NCard>
    <NTabs v-model:value="tab" type="line" animated class="plugin-manager-tabs">
      <NTabPane name="installed" tab="已安装插件">
        <NSpin v-if="pluginsQuery.isPending.value" class="center-spin" />
        <NAlert v-else-if="pluginsQuery.isError.value" type="error">插件列表加载失败，请刷新重试。</NAlert>
        <NEmpty v-else-if="!plugins.length" description="尚未安装插件，可上传 ZIP 或从 GitHub 仓库选择安装" class="plugin-manager-empty" />
        <div v-else class="plugin-card-grid"><NCard v-for="plugin in plugins" :key="plugin.pluginKey" class="plugin-card" :bordered="false">
          <div class="plugin-card-head"><div class="plugin-card-symbol">{{ plugin.runtime === 'jint' ? 'JS' : 'C#' }}</div><NTag :type="plugin.state === 'Active' ? 'success' : plugin.state === 'Failed' ? 'error' : 'default'" round>{{ plugin.state === 'Active' ? '运行中' : plugin.state === 'Disabled' ? '已禁用' : plugin.state === 'Draining' ? '正在停止' : '加载失败' }}</NTag></div>
          <h3>{{ plugin.name }}</h3><p class="plugin-card-description">{{ plugin.description || installedSource(plugin.pluginKey)?.description || '此插件尚未提供描述。' }}</p>
          <div class="plugin-card-meta"><span>{{ plugin.pluginKey }}</span><span>v{{ plugin.version }}</span></div>
          <div v-if="installedSource(plugin.pluginKey)" class="plugin-card-source">来源：{{ installedSource(plugin.pluginKey)?.owner }}/{{ installedSource(plugin.pluginKey)?.repo }} · {{ installedSource(plugin.pluginKey)?.tag }}</div>
          <div class="plugin-card-footer"><NButton v-if="plugin.tasks.length" text type="primary" @click="openTasks(plugin)">定时任务 · {{ plugin.tasks.length }}</NButton><span v-else class="plugin-card-muted">无定时任务</span>
            <div class="plugin-card-buttons"><NButton v-if="updateFor(plugin.pluginKey)" size="small" text type="primary" @click="tab = 'updates'">有更新</NButton><NButton size="small" secondary :type="plugin.state === 'Active' ? 'warning' : 'success'" :loading="togglingKey === plugin.pluginKey" :disabled="plugin.state === 'Draining' || Boolean(deletingKey)" @click="togglePlugin(plugin)">{{ plugin.state === 'Active' ? '禁用' : '启用' }}</NButton><NPopconfirm @positive-click="deletePlugin(plugin.pluginKey)"><template #trigger><NButton size="small" secondary type="error" :loading="deletingKey === plugin.pluginKey" :disabled="plugin.state === 'Draining' || Boolean(togglingKey)">删除</NButton></template>确定删除 {{ plugin.name }} 的插件文件？</NPopconfirm></div>
          </div>
        </NCard></div>
      </NTabPane>
      <NTabPane name="updates" tab="插件更新">
        <div class="plugin-update-toolbar"><span>选择要更新的插件，未勾选的插件保持当前版本。</span><div><NButton secondary :loading="updatesQuery.isFetching.value" @click="updatesQuery.refetch()">检查更新</NButton><NButton type="primary" :disabled="!selectedUpdateIds.length" :loading="updating" @click="updateSelected">更新所选 {{ selectedUpdateIds.length }} 个</NButton></div></div>
        <NSpin v-if="updatesQuery.isPending.value" class="center-spin" />
        <NAlert v-else-if="updatesQuery.isError.value" type="error">检查更新失败，请确认 GitHub 可访问后重试。</NAlert>
        <NEmpty v-else-if="!updates.length" description="已订阅插件暂无更新" class="plugin-manager-empty" />
        <div v-else class="plugin-card-grid"><NCard v-for="item in updates" :key="item.installed.pluginId" class="plugin-card plugin-update-card" :bordered="false">
          <div class="plugin-card-head"><NCheckbox :checked="selectedUpdateIds.includes(item.installed.pluginId)" :disabled="updating" @update:checked="checked => setSelected(item.installed.pluginId, checked, 'update')">选择更新</NCheckbox><NTag type="info" round>{{ item.available.runtime }}</NTag></div>
          <h3>{{ item.available.name }}</h3><p class="plugin-card-description">{{ item.available.description }}</p>
          <div class="plugin-card-meta"><span>{{ item.installed.pluginId }}</span><span>{{ item.installed.tag }} → {{ item.tag }}</span></div><div class="plugin-card-source">{{ item.installed.owner }}/{{ item.installed.repo }} · {{ item.available.asset }}</div>
        </NCard></div>
      </NTabPane>
    </NTabs>
    <NModal v-model:show="uploadModalOpen" preset="card" title="上传插件 ZIP" :closable="!uploading" :mask-closable="!uploading" :close-on-esc="!uploading" style="width: min(600px, calc(100vw - 32px))">
      <div class="plugin-subscribe-form">
        <NAlert type="warning" title="仅上传可信插件">C# / JS 插件可执行代码。相同插件 ID 会完整替换旧包并立即重新加载（已禁用插件也会启用）；加载失败自动恢复旧包。只影响本插件，不重载其他插件。本地覆盖会解除该插件的发行版订阅关联，保留仓库订阅。</NAlert>
        <p>支持 ZIP 内包含单个插件目录，或 plugin.json / DLL 直接位于 ZIP 根目录；每包一个插件，最大 100 MiB。</p>
        <label for="plugin-upload-file">插件 ZIP 文件</label>
        <input id="plugin-upload-file" ref="uploadInput" type="file" accept=".zip,application/zip" :disabled="uploading" @change="selectUpload" />
        <NAlert v-if="uploadError" type="error" role="alert">{{ uploadError }}</NAlert>
        <div class="modal-actions"><NButton :disabled="uploading" @click="uploadModalOpen = false">取消</NButton><NButton type="primary" :loading="uploading" :disabled="!uploadFile" @click="uploadPlugin">上传、覆盖并加载</NButton></div>
      </div>
    </NModal>
    <NModal v-model:show="subscribeModalOpen" preset="card" title="订阅 GitHub 插件仓库" style="width: min(760px, calc(100vw - 32px))">
      <div class="plugin-subscribe-form"><label for="plugin-repository-input">仓库地址</label><div class="plugin-subscribe-row"><NInput id="plugin-repository-input" v-model:value="repositoryInput" placeholder="owner/repo 或 https://github.com/owner/repo" @keyup.enter="addRepository" /><NButton :loading="savingRepository" @click="addRepository">添加仓库</NButton></div>
        <label for="plugin-repository-select">已订阅仓库</label><NSelect id="plugin-repository-select" v-model:value="selectedRepository" :options="repositoryOptions" placeholder="选择仓库" />
        <label for="plugin-release-select">发布版本</label><NSelect id="plugin-release-select" v-model:value="selectedTag" :options="releaseOptions" :loading="loadingReleases" :disabled="!selectedRepository" placeholder="选择 Release 版本" />
        <NAlert v-if="modalError" type="error">{{ modalError }}</NAlert><NSpin v-if="loadingIndex" class="center-spin" /><NEmpty v-else-if="selectedTag && !releaseIndex?.plugins.length" description="该版本没有可安装插件" />
        <div v-else-if="releaseIndex" class="plugin-choice-list"><label v-for="entry in releaseIndex.plugins" :key="entry.id" class="plugin-choice"><NCheckbox :checked="selectedPluginIds.includes(entry.id)" :disabled="installing" @update:checked="checked => setSelected(entry.id, checked, 'install')" /><span><strong>{{ entry.name }}</strong><small>{{ entry.description }}</small><small>{{ entry.id }} · {{ entry.runtime }} · v{{ entry.version }}</small></span></label></div>
        <div class="modal-actions"><NButton @click="subscribeModalOpen = false">取消</NButton><NButton type="primary" :disabled="!selectedPluginIds.length" :loading="installing" @click="installSelected">下载并安装 {{ selectedPluginIds.length }} 个插件</NButton></div>
      </div>
    </NModal>
    <NModal v-model:show="taskModalOpen" preset="card" :title="selectedTaskPlugin ? `${selectedTaskPlugin.name} · 定时任务` : '定时任务'" style="width: min(760px, calc(100vw - 32px))"><NEmpty v-if="selectedTaskPlugin && !selectedTaskPlugin.tasks.length" description="该插件没有注册定时任务" /><NDataTable v-else-if="selectedTaskPlugin" :columns="taskColumns" :data="selectedTaskPlugin.tasks" :bordered="false" /></NModal>
  </div>
</template>
