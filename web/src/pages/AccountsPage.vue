<script setup lang="ts">
import { computed, h, reactive, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useDialog, useMessage, NAlert, NButton, NCard, NDataTable, NEmpty, NForm, NFormItem, NInput, NIcon, NModal, NPagination, NSelect, NSpin, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type Account } from '@/services/api'
import { RefreshOutline } from '@vicons/ionicons5'
import ResponsiveFilterPanel from '@/components/ResponsiveFilterPanel.vue'

const message = useMessage(); const dialog = useDialog(); const queryClient = useQueryClient()
const page = ref(1); const pageSize = ref<20 | 50 | 100>(20); const filter = reactive({ pluginKey: '', platform: '', state: '', keyword: '' })
const query = useQuery({ queryKey: computed(() => ['accounts', page.value, pageSize.value, { ...filter }]), queryFn: () => api.accounts({ page: page.value, pageSize: pageSize.value, ...filter }) })
const platformsQuery = useQuery({ queryKey: ['platforms'], queryFn: api.platforms })
watch(() => [filter.pluginKey, filter.platform, filter.state, filter.keyword], () => { page.value = 1 })
const rows = computed(() => query.data.value?.items ?? [])
const platformOptions = computed(() => [{ label: '全部平台', value: '' }, ...(platformsQuery.data.value ?? []).map(item => ({ label: item.displayName, value: item.name }))])
const stateOptions = [{ label: '全部状态', value: '' }, { label: '启用', value: 'Active' }, { label: '冷却', value: 'Cooling' }, { label: '停用', value: 'Disabled' }]
const showModal = ref(false); const saving = ref(false); const editingAccountId = ref<string | null>(null)
const form = reactive({ platform: 'myai', label: '', credentialKind: 'apiKey', apiKey: '', username: '', password: '', accessToken: '', refreshToken: '', idToken: '', accountId: '', token: '', cookie: '', customJson: '{}' })
const credentialOptions = [{ label: 'API Key', value: 'apiKey' }, { label: '账号密码', value: 'basicAuth' }, { label: 'OAuth / Token 组', value: 'oauth' }, { label: 'Bearer Token', value: 'bearerToken' }, { label: 'Cookie', value: 'cookie' }, { label: '自定义 JSON', value: 'custom' }]
const credentialLabels: Record<string, string> = { apiKey: 'API Key', basicAuth: '账号密码', oauth: 'OAuth / Token 组', bearerToken: 'Bearer Token', cookie: 'Cookie', custom: '自定义凭证' }
const columns: DataTableColumns<Account> = [
  { title: '账号', key: 'label', render: row => row.label || row.id.slice(0, 10) },
  { title: '插件 / 平台', key: 'platform', render: row => `${row.pluginKey || '—'} / ${row.platform}` },
  { title: '凭证', key: 'credentialKind', render: row => h(NTag, { type: 'info', bordered: false }, { default: () => credentialLabels[formCredentialKind(row.credentialKind)] ?? row.credentialKind }) },
  { title: '状态', key: 'enabled', render: row => h(NTag, { type: row.enabled ? 'success' : 'default', round: true }, { default: () => row.state ?? (row.enabled ? '启用' : '停用') }) },
  { title: '操作', key: 'actions', width: 150, render: row => h('div', { class: 'table-actions' }, [h(NButton, { size: 'small', tertiary: true, onClick: () => openEdit(row) }, { default: () => '编辑' }), h(NButton, { size: 'small', tertiary: true, type: 'error', onClick: () => confirmDelete(row) }, { default: () => '删除' })]) },
]

function resetForm(platform = platformOptions.value[1]?.value ?? 'myai') { Object.assign(form, { platform, label: '', credentialKind: 'apiKey', apiKey: '', username: '', password: '', accessToken: '', refreshToken: '', idToken: '', accountId: '', token: '', cookie: '', customJson: '{}' }) }
function formCredentialKind(value: string) { const normalized = value.replace(/[_-]/g, '').toLowerCase(); return normalized === 'apikey' ? 'apiKey' : normalized === 'basicauth' ? 'basicAuth' : normalized === 'bearertoken' ? 'bearerToken' : normalized === 'cookie' ? 'cookie' : normalized === 'oauth' ? 'oauth' : 'custom' }
function openCreate() { editingAccountId.value = null; resetForm(); showModal.value = true }
function credentialString(account: Account, key: string) { const value = account.credential?.[key]; return value == null ? '' : String(value) }
function openEdit(account: Account) {
  editingAccountId.value = account.id
  resetForm(account.platform)
  form.label = account.label ?? ''
  form.credentialKind = formCredentialKind(account.credentialKind)
  form.apiKey = credentialString(account, 'apiKey')
  form.username = credentialString(account, 'username')
  form.password = credentialString(account, 'password')
  form.accessToken = credentialString(account, 'accessToken')
  form.refreshToken = credentialString(account, 'refreshToken')
  form.idToken = credentialString(account, 'idToken')
  form.accountId = credentialString(account, 'accountId')
  form.token = credentialString(account, 'token')
  form.cookie = credentialString(account, 'cookie')
  form.customJson = JSON.stringify(account.credential ?? {}, null, 2)
  showModal.value = true
}
function resetFilters() { Object.assign(filter, { pluginKey: '', platform: '', state: '', keyword: '' }); page.value = 1 }
async function save() {
  const credential: Record<string, unknown> = {}
  if (form.credentialKind === 'apiKey' && form.apiKey.trim()) credential.apiKey = form.apiKey.trim()
  if (form.credentialKind === 'basicAuth' && (form.username.trim() || form.password)) Object.assign(credential, { username: form.username.trim(), password: form.password })
  if (form.credentialKind === 'oauth' && (form.accessToken.trim() || form.refreshToken.trim() || form.idToken.trim() || form.accountId.trim())) Object.assign(credential, { access_token: form.accessToken.trim(), refresh_token: form.refreshToken.trim() || undefined, id_token: form.idToken.trim() || undefined, account_id: form.accountId.trim() || undefined })
  if (form.credentialKind === 'bearerToken' && form.token.trim()) credential.token = form.token.trim()
  if (form.credentialKind === 'cookie' && form.cookie.trim()) credential.cookie = form.cookie.trim()
  if (form.credentialKind === 'custom' && form.customJson.trim() && form.customJson.trim() !== '{}') { try { Object.assign(credential, JSON.parse(form.customJson)) } catch { message.warning('凭证 JSON 格式不正确'); return } }
  if (!Object.keys(credential).length) { message.warning('请填写凭证信息'); return }
  saving.value = true
  try { await api.saveAccount(form.platform, { id: editingAccountId.value ?? undefined, label: form.label.trim(), credentialKind: form.credentialKind, ...credential }); await queryClient.invalidateQueries({ queryKey: ['accounts'] }); showModal.value = false; message.success(editingAccountId.value ? '账号已更新' : '账号已保存') } catch (error) { message.error(error instanceof Error ? error.message : '保存失败') } finally { saving.value = false }
}
function confirmDelete(account: Account) { dialog.warning({ title: '删除账号', content: `确定删除“${account.label || account.id}”吗？`, positiveText: '删除', negativeText: '取消', onPositiveClick: async () => { try { await api.deleteAccount(account.id, account.pluginKey); await queryClient.invalidateQueries({ queryKey: ['accounts'] }); message.success('账号已删除') } catch (error) { message.error(error instanceof Error ? error.message : '删除失败') } } }) }
</script>

<template>
  <div class="table-page">
  <NCard title="账号列表" :bordered="false" class="table-card">
    <template #header-extra><NButton type="primary" @click="openCreate">新增账号</NButton></template>
    <ResponsiveFilterPanel title="筛选账号"><div class="table-filter-heading"><div><strong>筛选账号</strong><span>按平台、状态或关键词快速定位账户</span></div><NButton quaternary size="small" @click="resetFilters">清空筛选</NButton></div><div class="filter-row"><NSelect v-model:value="filter.platform" :options="platformOptions" placeholder="平台" /><NSelect v-model:value="filter.state" :options="stateOptions" placeholder="状态" /><NInput v-model:value="filter.keyword" clearable placeholder="搜索账号或备注" @keyup.enter="page = 1" /></div></ResponsiveFilterPanel>
    <NSpin v-if="query.isPending.value" /><NAlert v-else-if="query.isError.value" type="error" title="账号加载失败">请刷新后重试。</NAlert><div v-else class="table-data-wrap"><NDataTable class="table-data-grid" :columns="columns" :data="rows" :scroll-x="720" :flex-height="true" :bordered="false"><template #empty><NEmpty description="还没有账号，先添加一个平台凭证吧" /></template></NDataTable></div>
    <div class="table-footer"><span>共 {{ query.data.value?.total ?? 0 }} 个账号</span><div class="table-pagination"><NButton secondary circle :loading="query.isFetching.value" aria-label="刷新账号列表" title="刷新" @click="query.refetch()"><template #icon><NIcon :component="RefreshOutline" /></template></NButton><NPagination v-model:page="page" v-model:page-size="pageSize" :page-count="query.data.value?.totalPages ?? 1" :page-sizes="[20, 50, 100]" show-size-picker /></div></div>
  </NCard>
  </div>
  <NModal v-model:show="showModal" preset="card" :title="editingAccountId ? '编辑账号' : '新增账号'" style="width: min(560px, calc(100vw - 32px))"><NForm label-placement="top"><NFormItem label="平台"><NSelect v-model:value="form.platform" :options="platformOptions.slice(1)" :disabled="Boolean(editingAccountId)" /></NFormItem><NFormItem label="显示名称"><NInput v-model:value="form.label" /></NFormItem><NFormItem label="凭证类型"><NSelect v-model:value="form.credentialKind" :options="credentialOptions" /></NFormItem><NAlert v-if="editingAccountId" type="info" :bordered="false" class="account-edit-hint">当前凭证已回显，可直接修改后保存。</NAlert><NFormItem v-if="form.credentialKind === 'apiKey'" label="API Key"><NInput v-model:value="form.apiKey" placeholder="请输入 API Key" /></NFormItem><div v-else-if="form.credentialKind === 'basicAuth'" class="form-row"><NFormItem label="账号" class="grow"><NInput v-model:value="form.username" /></NFormItem><NFormItem label="密码" class="grow"><NInput v-model:value="form.password" /></NFormItem></div><div v-else-if="form.credentialKind === 'oauth'" class="account-credential-grid"><NFormItem label="Access Token"><NInput v-model:value="form.accessToken" /></NFormItem><NFormItem label="Refresh Token"><NInput v-model:value="form.refreshToken" /></NFormItem><NFormItem label="ID Token"><NInput v-model:value="form.idToken" /></NFormItem><NFormItem label="Account ID"><NInput v-model:value="form.accountId" /></NFormItem></div><NFormItem v-else-if="form.credentialKind === 'bearerToken'" label="Bearer Token"><NInput v-model:value="form.token" /></NFormItem><NFormItem v-else-if="form.credentialKind === 'cookie'" label="Cookie"><NInput v-model:value="form.cookie" /></NFormItem><NFormItem v-else label="凭证 JSON"><NInput v-model:value="form.customJson" type="textarea" :autosize="{ minRows: 5, maxRows: 10 }" placeholder='{"token":"..."}' /></NFormItem><div class="modal-actions"><NButton @click="showModal = false">取消</NButton><NButton type="primary" :loading="saving" @click="save">{{ editingAccountId ? '保存修改' : '保存账号' }}</NButton></div></NForm></NModal>
</template>
