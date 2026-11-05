<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NDataTable, NEmpty, NFormItem, NInput, NPagination, NSelect, NSpin, NSwitch, NTabPane, NTabs, NTag, useMessage } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { api, type ObservedRequest, type PrivacySettings } from '@/services/api'
import { privacyActions, privacyCategories } from '@/services/observationLabels'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const tab = ref('overview')
const range = ref('24h')
const page = ref(1)
const ranges = [{ label: '最近 24 小时', value: '24h' }, { label: '最近 7 天', value: '7d' }, { label: '最近 30 天', value: '30d' }]
function bounds() {
  const toUtc = new Date()
  return { fromUtc: new Date(toUtc.getTime() - (range.value === '7d' ? 7 : range.value === '30d' ? 30 : 1) * 86400000), toUtc }
}
const settings = useQuery({ queryKey: ['privacy-settings'], queryFn: api.privacySettings })
const draft = ref<PrivacySettings>()
watch(settings.data, value => {
  if (value) draft.value = { ...value, categoryActions: { ...value.categoryActions }, customRules: value.customRules.map(rule => ({ ...rule })) }
}, { immediate: true })
const summary = useQuery({ queryKey: computed(() => ['privacy-summary', range.value]), queryFn: () => api.privacySummary(bounds()), refetchInterval: 60_000 })
const events = useQuery({ queryKey: computed(() => ['privacy-events', range.value, page.value]), queryFn: () => api.privacyEvents({ ...bounds(), page: page.value }), refetchInterval: 30_000 })
watch(range, () => { page.value = 1 })
const saving = ref(false)
async function save() {
  if (!draft.value) return
  saving.value = true
  try { draft.value = await api.savePrivacySettings(draft.value); await settings.refetch(); message.success('隐私护栏设置已保存') }
  catch (error) { message.error(error instanceof Error ? error.message : '设置保存失败') }
  finally { saving.value = false }
}
function addRule() {
  draft.value?.customRules.push({ id: crypto.randomUUID(), name: '', pattern: '', enabled: true, action: null })
}
const input = ref('')
const testing = ref(false)
const testError = ref('')
const result = ref<Awaited<ReturnType<typeof api.privacyTest>>>()
async function test() {
  testing.value = true; testError.value = ''; result.value = undefined
  try { result.value = await api.privacyTest(input.value) }
  catch (error) { testError.value = error instanceof Error ? error.message : '检测失败' }
  finally { testing.value = false }
}
const actionName = (value: string) => privacyActions.find(action => action.value === value)?.label ?? value
const columns: DataTableColumns<ObservedRequest> = [
  { title: '时间', key: 'createdAt', render: row => new Date(row.createdAt).toLocaleString('zh-CN') },
  { title: '模型', key: 'model', render: row => row.model ?? '—' },
  { title: '处理结果', key: 'privacy', render: row => h(NTag, { size: 'small', bordered: false, type: row.privacy?.blocked ? 'error' : 'warning' },
    { default: () => row.privacy?.error ? '检测失败' : row.privacy?.dryRun ? '仅检测' : row.privacy?.blocked ? '已拦截' : row.privacy?.redactions ? '已脱敏' : '告警' }) },
  { title: '命中类别', key: 'hits', render: row => row.privacy?.hits.map(hit => `${privacyCategories[hit.category] ?? hit.category} ×${hit.count}`).join('、') || '—' },
  { title: '追踪', key: 'trace', render: row => h(RouterLink, { to: `/traces/requests/${row.id}`, class: 'dashboard-link' }, { default: () => '查看请求' }) },
]
</script>

<template>
  <PageHeader title="隐私护栏" description="在请求进入插件前检查敏感文本，记录告警、阻断泄露或脱敏后还原响应。" />
  <NAlert type="info" :show-icon="false" class="mb-20">保护对话与工具文本；不检查认证头、图片或文件二进制。语音合成不受保护。匹配原文只在单次请求内存中存在，不保存到日志。</NAlert>
  <NTabs v-model:value="tab" type="line" animated>
    <NTabPane name="overview" tab="概览">
      <div class="observation-toolbar"><NSelect v-model:value="range" :options="ranges" aria-label="统计范围" /></div>
      <NAlert v-if="summary.isError.value" type="error" title="护栏统计加载失败"><NButton text @click="summary.refetch()">重试</NButton></NAlert>
      <NSpin v-else-if="summary.isPending.value" />
      <template v-else-if="summary.data.value">
        <div class="metrics-grid privacy-metrics">
          <NCard v-for="metric in [{ label: '检测请求', value: summary.data.value.inspected }, { label: '命中请求', value: summary.data.value.matched }, { label: '拦截请求', value: summary.data.value.blocked }, { label: '脱敏替换', value: summary.data.value.redactions }]" :key="metric.label" :bordered="false" class="metric-card">
            <div class="metric-label">{{ metric.label }}</div><div class="metric-value">{{ metric.value.toLocaleString('zh-CN') }}</div>
          </NCard>
        </div>
        <NCard title="命中分布" :bordered="false" class="dashboard-card">
          <NEmpty v-if="!summary.data.value.categories.length" description="暂无命中。启用护栏后，检测结果会显示在这里。" />
          <div v-for="category in summary.data.value.categories" :key="category.category" class="privacy-category-row">
            <span>{{ privacyCategories[category.category] ?? category.category }}</span><strong>{{ category.hits.toLocaleString('zh-CN') }} 次</strong>
          </div>
          <p class="metric-note">仅检测模式处理 {{ summary.data.value.dryRun }} 次请求 · 统计基于已保留日志。</p>
        </NCard>
      </template>
    </NTabPane>
    <NTabPane name="logs" tab="事件日志">
      <div class="observation-toolbar"><NSelect v-model:value="range" :options="ranges" aria-label="事件范围" /></div>
      <NAlert v-if="events.isError.value" type="error" title="事件日志加载失败"><NButton text @click="events.refetch()">重试</NButton></NAlert>
      <template v-else>
        <NDataTable :columns="columns" :data="events.data.value?.items ?? []" :loading="events.isPending.value" :bordered="false" :scroll-x="800" />
        <NPagination v-model:page="page" :page-size="20" :item-count="events.data.value?.total ?? 0" class="mt-16" />
      </template>
    </NTabPane>
    <NTabPane name="rules" tab="检测规则">
      <NAlert v-if="settings.isError.value" type="error" title="设置加载失败"><NButton text @click="settings.refetch()">重试</NButton></NAlert>
      <NSpin v-else-if="!draft" />
      <template v-else>
        <NCard :bordered="false" class="settings-card mb-20">
          <div class="privacy-switch-row"><div><strong>启用隐私护栏</strong><p class="metric-note">默认关闭，不改变升级前的请求行为。</p></div><NSwitch v-model:value="draft.enabled" aria-label="启用隐私护栏" /></div>
          <div class="privacy-switch-row"><div><strong>仅检测模式</strong><p class="metric-note">只检测和记录，不阻断、不修改正文。确认规则后再关闭此开关。</p></div><NSwitch v-model:value="draft.dryRun" aria-label="仅检测模式" /></div>
          <NFormItem label="全局默认动作"><NSelect v-model:value="draft.defaultAction" :options="privacyActions" /></NFormItem>
        </NCard>
        <NCard title="分类动作" :bordered="false" class="settings-card mb-20">
          <div class="privacy-category-grid">
            <NFormItem v-for="(label, category) in privacyCategories" :key="category" :label="label">
              <NSelect :value="draft.categoryActions[category] ?? null" :options="privacyActions" clearable placeholder="继承全局动作"
                @update:value="value => { if (value) draft!.categoryActions[category] = value; else delete draft!.categoryActions[category] }" />
            </NFormItem>
          </div>
        </NCard>
        <NCard title="自定义正则" :bordered="false" class="settings-card mb-20">
          <p class="metric-note">规则优先于分类动作。最多 100 条，每条最长 2048 字符；执行超时会明确拒绝请求，不会放行。</p>
          <div v-for="(rule, index) in draft.customRules" :key="rule.id" class="privacy-custom-rule">
            <NInput v-model:value="rule.name" placeholder="规则名称" :aria-label="`规则 ${index + 1} 名称`" :maxlength="80" />
            <NInput v-model:value="rule.pattern" placeholder="正则表达式" :aria-label="`规则 ${index + 1} 正则`" :maxlength="2048" />
            <NSelect v-model:value="rule.action" :options="privacyActions" clearable placeholder="继承分类" :aria-label="`规则 ${index + 1} 动作`" />
            <NSwitch v-model:value="rule.enabled" :aria-label="`启用规则 ${index + 1}`" />
            <NButton quaternary type="error" @click="draft.customRules.splice(index, 1)">删除</NButton>
          </div>
          <NButton secondary :disabled="draft.customRules.length >= 100" @click="addRule">添加规则</NButton>
        </NCard>
        <NButton type="primary" :loading="saving" @click="save">保存设置</NButton>
      </template>
    </NTabPane>
    <NTabPane name="tester" tab="试用检测">
      <NCard :bordered="false" class="settings-card privacy-tester">
        <NFormItem label="检测文本"><NInput v-model:value="input" type="textarea" :rows="7" :maxlength="65536" show-count placeholder="粘贴需要检查的文本。不会调用插件或保存输入。" /></NFormItem>
        <NButton type="primary" :loading="testing" :disabled="!input.trim()" @click="test">检测文本</NButton>
        <NAlert v-if="testError" type="error" title="检测失败" class="mt-16">{{ testError }}</NAlert>
        <template v-if="result">
          <NAlert :type="result.report.blocked ? 'error' : result.report.hits.length ? 'warning' : 'success'" class="mt-16">
            {{ result.report.blocked ? '正式请求将被拦截' : result.report.dryRun ? '当前为仅检测模式，正文保持不变' : result.report.redactions ? '正文已脱敏，正式响应会还原' : '检测完成' }}
          </NAlert>
          <div v-for="hit in result.report.hits" :key="hit.ruleId" class="privacy-category-row"><span>{{ privacyCategories[hit.category] }} · {{ actionName(hit.action) }}</span><strong>{{ hit.count }} 次</strong></div>
          <NEmpty v-if="!result.report.hits.length" description="没有规则命中" class="mt-16" />
          <pre class="privacy-test-output">{{ result.text }}</pre>
        </template>
      </NCard>
    </NTabPane>
  </NTabs>
</template>
