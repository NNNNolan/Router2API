<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NEmpty, NForm, NFormItem, NInput, NInputNumber, NModal, NPopconfirm, NSelect, NSpin, NSwitch, NTag, useMessage } from 'naive-ui'
import { api, type ModelMapping, type ModelMappingTarget } from '@/services/api'

const message = useMessage()
const queryClient = useQueryClient()
const query = useQuery({ queryKey: ['model-mappings'], queryFn: api.modelMappings })
const platforms = useQuery({ queryKey: ['platforms'], queryFn: api.platforms })
type CooldownUnit = 1 | 60 | 3600
type EditableTarget = Omit<ModelMappingTarget, 'failureThreshold' | 'cooldownSeconds'> & {
  failureThreshold: number | null; cooldownValue: number | null; cooldownUnit: CooldownUnit
}
type EditableMapping = Omit<ModelMapping, 'targets'> & { targets: EditableTarget[] }
const maxInteger = 2_147_483_647
const cooldownUnits = [{ label: '小时', value: 3600 }, { label: '分钟', value: 60 }, { label: '秒', value: 1 }]
const editing = ref<EditableMapping | null>(null)
const models = useQuery({ queryKey: ['model-plaza'], queryFn: api.modelPlaza, enabled: computed(() => editing.value !== null), staleTime: 60_000 })
const keyword = ref('')
const saving = ref(false)
const busyId = ref<string | null>(null)
const formError = ref('')
const mappings = computed(() => (query.data.value ?? []).filter(mapping => mapping.name.toLowerCase().includes(keyword.value.trim().toLowerCase())))
const locked = computed(() => saving.value || busyId.value !== null)

function durationUnit(seconds: number): CooldownUnit { return seconds > 0 && seconds % 3600 === 0 ? 3600 : seconds % 60 === 0 ? 60 : 1 }
function editableTarget(target: ModelMappingTarget): EditableTarget {
  const { cooldownSeconds, ...values } = target
  const cooldownUnit = durationUnit(cooldownSeconds)
  return { ...values, cooldownValue: cooldownSeconds / cooldownUnit, cooldownUnit }
}
function blankTarget(): EditableTarget { return editableTarget({ pluginKey: '', platform: '', model: '', enabled: true, retryCount: null, failureThreshold: 3, cooldownSeconds: 300 }) }
function cooldownSummary(target: ModelMappingTarget) {
  if (target.failureThreshold === 0 || target.cooldownSeconds === 0) return '不自动冷却'
  const unit = durationUnit(target.cooldownSeconds)
  return `连续 ${target.failureThreshold} 轮失败，冷却 ${target.cooldownSeconds / unit} ${cooldownUnits.find(option => option.value === unit)!.label}`
}
function edit(mapping?: ModelMapping) {
  formError.value = ''
  editing.value = mapping ? { ...mapping, targets: mapping.targets.map(editableTarget) }
    : { id: '', name: '', enabled: true, targets: [blankTarget()] }
}
function closeEditor() {
  if (!locked.value) editing.value = null
}
function platformOptions(target: EditableTarget) {
  const options = (platforms.data.value ?? []).map(platform => ({ value: platform.name, label: `${platform.displayName || platform.name} · ${platform.pluginKey}${platform.enabled ? '' : '（已停用）'}` }))
  if (target.platform && !options.some(option => option.value === target.platform)) options.push({ value: target.platform, label: `${target.platform} · ${target.pluginKey}（未加载）` })
  return options
}
function selectPlatform(target: EditableTarget, name: string | null) {
  const platform = platforms.data.value?.find(platform => platform.name === name)
  target.platform = name ?? ''
  target.pluginKey = platform?.pluginKey ?? ''
  target.model = ''
}
function selectModel(target: EditableTarget, model: string | null) {
  target.model = model ?? ''
  if (editing.value && !editing.value.name.trim())
    editing.value.name = target.model.slice(target.model.lastIndexOf('/') + 1).trim()
}
function modelOptions(target: EditableTarget) {
  const prefix = `${target.platform}/`
  const options = (models.data.value?.models ?? []).filter(model => model.id.startsWith(prefix)).map(model => ({ value: model.id.slice(prefix.length), label: model.id.slice(prefix.length) }))
  if (target.model && !options.some(option => option.value === target.model)) options.push({ value: target.model, label: target.model })
  return options
}
function unavailable(target: Pick<ModelMappingTarget, 'platform' | 'pluginKey'>) {
  if (!platforms.data.value) return false
  return !platforms.data.value.some(platform => platform.enabled && platform.name.toLowerCase() === target.platform.toLowerCase() && platform.pluginKey.toLowerCase() === target.pluginKey.toLowerCase())
}
function move(index: number, direction: number) {
  const targets = editing.value!.targets
  const [target] = targets.splice(index, 1)
  targets.splice(index + direction, 0, target!)
}
async function refreshMappings() {
  await queryClient.invalidateQueries({ queryKey: ['model-mappings'] })
}
async function save() {
  const mapping = editing.value
  if (!mapping) return
  formError.value = ''
  if (!/^[a-zA-Z0-9._:-]{1,128}$/.test(mapping.name.trim())) { formError.value = '对外模型名须为 1–128 个英文字母、数字或 . _ - :，不能包含 /。'; return }
  if (!mapping.targets.length || mapping.targets.some(target => !target.pluginKey || !target.platform || !target.model.trim())) { formError.value = '请为每个目标选择平台并填写插件内模型。'; return }
  if (mapping.enabled && !mapping.targets.some(target => target.enabled)) { formError.value = '启用的映射至少需要一个启用的目标。'; return }
  if (mapping.targets.some(target => target.failureThreshold === null || !Number.isInteger(target.failureThreshold)
    || target.failureThreshold < 0 || target.failureThreshold > maxInteger || target.cooldownValue === null
    || !Number.isInteger(target.cooldownValue) || target.cooldownValue < 0 || target.cooldownValue > Math.floor(maxInteger / target.cooldownUnit))) {
    formError.value = '请填写有效的非负整数轮数和冷却时间；任一项为 0 表示不冷却。'
    return
  }
  saving.value = true
  try {
    await api.saveModelMapping({ ...mapping, name: mapping.name.trim(), targets: mapping.targets.map(({ cooldownValue, cooldownUnit, ...target }) => ({
      ...target, model: target.model.trim(), failureThreshold: target.failureThreshold!, cooldownSeconds: cooldownValue! * cooldownUnit,
    })) })
    editing.value = null
    await refreshMappings()
    message.success('模型映射已保存')
  } catch (error) { formError.value = error instanceof Error ? error.message : '保存失败'; }
  finally { saving.value = false }
}
async function toggle(mapping: ModelMapping) {
  busyId.value = mapping.id
  try {
    await api.saveModelMapping({ ...mapping, enabled: !mapping.enabled })
    await refreshMappings()
    message.success(mapping.enabled ? '模型映射已停用' : '模型映射已启用')
  } catch (error) { message.error(error instanceof Error ? error.message : '状态更新失败') }
  finally { busyId.value = null }
}
async function remove(mapping: ModelMapping) {
  busyId.value = mapping.id
  try {
    await api.deleteModelMapping(mapping.id)
    if (editing.value?.id === mapping.id) editing.value = null
    await refreshMappings()
    message.success('模型映射已删除')
  } catch (error) { message.error(error instanceof Error ? error.message : '删除失败') }
  finally { busyId.value = null }
}
</script>

<template>
  <section class="model-mappings-page">
    <div class="page-heading">
      <div><h2>模型映射</h2><p>按目标顺序调用插件，成功后返回；可为每个目标单独设置失败轮数与冷却时间。</p></div>
      <NButton type="primary" :disabled="locked || editing !== null" @click="edit()">新建映射</NButton>
    </div>

    <NModal v-if="editing" :show="true" :mask-closable="false" :close-on-esc="!locked" @update:show="show => { if (!show) closeEditor() }">
      <NCard class="mapping-editor" size="small" :title="editing.id ? '编辑模型映射' : '新建模型映射'" :closable="!locked" role="dialog" aria-modal="true" :aria-label="editing.id ? '编辑模型映射' : '新建模型映射'" @close="closeEditor">
        <NForm class="mapping-editor-form" label-placement="top" size="small" :show-feedback="false" :disabled="locked" @submit.prevent="save">
          <div class="mapping-editor-body">
            <div class="mapping-basics">
              <NFormItem label="对外模型名" :label-props="{ for: 'mapping-name' }" required><NInput v-model:value="editing.name" placeholder="留空时根据首次选择的模型自动填写" :maxlength="128" aria-label="对外模型名" :input-props="{ id: 'mapping-name', 'aria-label': '对外模型名' }" /></NFormItem>
              <NFormItem label="启用映射"><NSwitch v-model:value="editing.enabled" size="small" aria-label="启用映射" /></NFormItem>
            </div>
            <p class="mapping-help">客户端的 model 填写此名称。尝试次数包含首次请求，1 表示只调用一次，留空继承插件；仅对允许重试的失败重试或切换，返回流后不再切换。</p>
            <div class="mapping-target-heading"><h3>调用顺序</h3><NButton size="small" :disabled="locked || editing.targets.length >= 32" @click="editing.targets.push(blankTarget())">添加目标</NButton></div>
            <ol class="mapping-target-editor">
              <li v-for="(target, index) in editing.targets" :key="index" class="mapping-target-form">
                <div class="mapping-target-toolbar">
                  <strong>目标 {{ index + 1 }}</strong>
                  <div class="mapping-actions">
                    <NSwitch v-model:value="target.enabled" size="small" :aria-label="`启用目标 ${index + 1}`" :disabled="locked" />
                    <NButton size="small" :disabled="locked || index === 0" :aria-label="`上移目标 ${index + 1}`" @click="move(index, -1)">上移</NButton>
                    <NButton size="small" :disabled="locked || index === editing.targets.length - 1" :aria-label="`下移目标 ${index + 1}`" @click="move(index, 1)">下移</NButton>
                    <NButton size="small" type="error" quaternary :disabled="locked || editing.targets.length === 1" :aria-label="`移除目标 ${index + 1}`" @click="editing.targets.splice(index, 1)">移除</NButton>
                  </div>
                </div>
                <div class="mapping-target-fields">
                  <NFormItem label="插件 / 平台" :label-props="{ for: `mapping-platform-${index}` }" required><NSelect :value="target.platform || null" :options="platformOptions(target)" filterable :loading="platforms.isPending.value" :input-props="{ id: `mapping-platform-${index}`, 'aria-label': `目标 ${index + 1} 平台` }" placeholder="选择插件平台" @update:value="selectPlatform(target, $event)" /></NFormItem>
                  <NFormItem label="插件内模型" :label-props="{ for: `mapping-model-${index}` }" required><NSelect :value="target.model" :options="modelOptions(target)" filterable tag :loading="models.isFetching.value" :input-props="{ id: `mapping-model-${index}`, 'aria-label': `目标 ${index + 1} 模型` }" placeholder="选择或输入模型 ID" @update:value="selectModel(target, $event)" /></NFormItem>
                  <NFormItem label="尝试次数（含首次）" :label-props="{ for: `mapping-attempts-${index}` }"><NInputNumber v-model:value="target.retryCount" :min="1" :max="35" :precision="0" :show-button="false" clearable placeholder="继承插件" :input-props="{ id: `mapping-attempts-${index}`, 'aria-label': `目标 ${index + 1} 尝试次数` }" /></NFormItem>
                </div>
                <div class="mapping-cooldown-fields">
                  <NFormItem label="连续失败轮数" :label-props="{ for: `mapping-failures-${index}` }"><NInputNumber v-model:value="target.failureThreshold" :min="0" :max="maxInteger" :precision="0" :show-button="false" placeholder="3" :input-props="{ id: `mapping-failures-${index}`, 'aria-label': `目标 ${index + 1} 连续失败轮数` }" /></NFormItem>
                  <NFormItem label="冷却时间" :label-props="{ for: `mapping-cooldown-${index}` }">
                    <div class="mapping-duration-input">
                      <NInputNumber v-model:value="target.cooldownValue" :min="0" :max="Math.floor(maxInteger / target.cooldownUnit)" :precision="0" :show-button="false" placeholder="5" :input-props="{ id: `mapping-cooldown-${index}`, 'aria-label': `目标 ${index + 1} 冷却时间` }" />
                      <NSelect v-model:value="target.cooldownUnit" :options="cooldownUnits" :input-props="{ 'aria-label': `目标 ${index + 1} 冷却单位` }" />
                    </div>
                  </NFormItem>
                </div>
                <p class="mapping-help">{{ target.failureThreshold === 0 || target.cooldownValue === 0 ? '此目标不自动冷却。' : '连续失败达到设定轮数后暂停调用；轮数或时间为 0 时不冷却。' }}</p>
                <p v-if="target.retryCount !== null" class="mapping-help">此目标最多调用 {{ target.retryCount }} 次，包含首次请求。</p>
                <p v-if="target.platform && unavailable(target)" class="mapping-unavailable">此插件平台当前不可用，请求时会跳过。</p>
              </li>
            </ol>
            <NAlert v-if="platforms.isError.value" type="error" class="mapping-feedback">平台加载失败，无法选择新目标。<NButton text @click="platforms.refetch()">重新加载</NButton></NAlert>
            <NAlert v-if="models.isError.value" type="warning" class="mapping-feedback">模型目录加载失败，可手动输入模型 ID。<NButton text @click="models.refetch()">重试</NButton></NAlert>
          </div>
          <div class="mapping-editor-footer">
            <NAlert v-if="formError" type="error" role="alert" class="mapping-feedback">{{ formError }}</NAlert>
            <div class="mapping-actions mapping-editor-actions"><NButton size="small" :disabled="locked" @click="closeEditor">取消</NButton><NButton size="small" type="primary" :loading="saving" :disabled="busyId !== null" attr-type="submit">保存映射</NButton></div>
          </div>
        </NForm>
      </NCard>
    </NModal>

    <NInput v-model:value="keyword" clearable placeholder="搜索对外模型名" :input-props="{ 'aria-label': '搜索模型映射' }" class="mapping-search" />
    <NSpin v-if="query.isPending.value" class="center-spin" />
    <NAlert v-else-if="query.isError.value" type="error">{{ query.error.value?.message || '模型映射加载失败' }} <NButton text @click="query.refetch()">重新加载</NButton></NAlert>
    <NEmpty v-else-if="!mappings.length" :description="keyword ? '没有匹配的模型映射' : '暂无模型映射，点击新建映射添加第一个对外模型。'" />
    <div v-else class="mapping-list">
      <article v-for="mapping in mappings" :key="mapping.id" class="mapping-card">
        <div class="mapping-card-heading">
          <div class="mapping-name"><code>{{ mapping.name }}</code><NTag :type="mapping.enabled ? 'success' : 'default'" size="small">{{ mapping.enabled ? '已启用' : '已停用' }}</NTag></div>
          <div class="mapping-actions">
            <NButton :disabled="locked || editing !== null" @click="edit(mapping)">编辑</NButton>
            <NButton :disabled="locked || editing !== null" @click="toggle(mapping)">{{ mapping.enabled ? '停用' : '启用' }}</NButton>
            <NPopconfirm @positive-click="remove(mapping)"><template #trigger><NButton type="error" quaternary :disabled="locked">删除</NButton></template>删除映射 {{ mapping.name }}？客户端将无法再通过此映射调用。</NPopconfirm>
          </div>
        </div>
        <ol class="mapping-route-list">
          <li v-for="(target, index) in mapping.targets" :key="index" :class="{ 'mapping-target-inactive': !target.enabled }">
            <span class="mapping-order">{{ index + 1 }}</span><div class="mapping-route-model"><code>{{ target.model }}</code><span>{{ target.pluginKey }} / {{ target.platform }}</span></div>
            <div class="mapping-policy"><span class="mapping-retry">{{ target.retryCount === null ? '继承插件策略' : `最多尝试 ${target.retryCount} 次` }}</span><span class="mapping-cooldown">{{ cooldownSummary(target) }}</span></div>
            <NTag v-if="!target.enabled" size="small">已停用</NTag><NTag v-else-if="unavailable(target)" type="warning" size="small">平台不可用</NTag>
          </li>
        </ol>
      </article>
    </div>
  </section>
</template>

<style scoped>
.model-mappings-page { display: grid; gap: 20px; }
.mapping-editor { width: min(980px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); overflow: hidden; }
.mapping-editor :deep(.n-card-header) { flex: none; padding: 14px 16px; }
.mapping-editor :deep(.n-card-header__main) { font-size: 15px; }
.mapping-editor :deep(.n-card__content) { display: flex; min-height: 0; padding: 0; }
.mapping-editor :deep(.n-form-item-label) { padding-bottom: 5px; font-size: 12px; }
.mapping-editor-form { display: flex; flex: 1; flex-direction: column; min-width: 0; min-height: 0; max-height: calc(100dvh - 92px); }
.mapping-editor-body { min-height: 0; padding: 0 16px 14px; overflow-y: auto; overscroll-behavior: contain; }
.mapping-editor-footer { flex: none; padding: 12px 16px; border-top: 1px solid var(--border); }
.mapping-editor-footer .mapping-feedback { margin: 0 0 10px; }
.mapping-search { max-width: 360px; }
.mapping-list { display: grid; gap: 16px; }
.mapping-card { padding: 18px; border: 1px solid var(--border); border-radius: 12px; background: var(--surface); min-width: 0; }
.mapping-card-heading, .mapping-target-heading, .mapping-target-toolbar, .mapping-name, .mapping-actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.mapping-card-heading, .mapping-target-heading, .mapping-target-toolbar { justify-content: space-between; }
.mapping-name > code, .mapping-route-model > code { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; overflow-wrap: anywhere; }
.mapping-name > code { font-size: 15px; font-weight: 600; }
.mapping-route-model > code { font-size: 13px; line-height: 1.5; }
.mapping-basics { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 16px; margin-bottom: 10px; }
.mapping-help { color: var(--muted); margin: 0 0 10px; font-size: 12px; line-height: 1.5; }
.mapping-target-heading h3 { margin: 6px 0; font-size: 13px; }
.mapping-target-editor, .mapping-route-list { padding: 0; margin: 16px 0 0; list-style: none; }
.mapping-target-editor { display: grid; gap: 10px; margin-top: 10px; }
.mapping-target-form { border: 1px solid var(--border); border-radius: 8px; padding: 12px; }
.mapping-target-toolbar strong { font-size: 13px; }
.mapping-target-fields { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) 130px; gap: 12px; margin: 10px 0; }
.mapping-target-fields :deep(.n-form-item-blank), .mapping-target-fields :deep(.n-input-number) { min-width: 0; width: 100%; }
.mapping-cooldown-fields { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; margin-bottom: 8px; }
.mapping-cooldown-fields :deep(.n-input-number) { width: 100%; }
.mapping-duration-input { display: flex; gap: 6px; width: 100%; min-width: 0; }
.mapping-duration-input .n-input-number { flex: 1; min-width: 0; }
.mapping-duration-input .n-select { flex: none; width: 90px; }
.mapping-policy { display: grid; gap: 4px; max-width: 100%; text-align: right; }
.mapping-route-list li { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; padding: 10px 0; border-top: 1px solid var(--border); }
.mapping-order { display: grid; place-items: center; flex: none; width: 24px; height: 24px; border-radius: 50%; background: var(--primary-soft); color: var(--primary); font-size: 12px; font-variant-numeric: tabular-nums; font-weight: 600; }
.mapping-route-model { display: grid; gap: 3px; flex: 1; min-width: 120px; overflow-wrap: anywhere; }
.mapping-route-model span, .mapping-retry, .mapping-cooldown { color: var(--muted); font-size: 12px; }
.mapping-target-inactive { color: var(--muted); }
.mapping-unavailable { color: var(--warning); font-size: 12px; margin: 0; }
.mapping-feedback { margin-top: 16px; }
.mapping-editor-actions { justify-content: flex-end; }
@media (max-width: 760px) {
  .mapping-target-fields { grid-template-columns: minmax(0, 1fr); gap: 10px; }
  .mapping-cooldown-fields { grid-template-columns: minmax(0, 1fr); gap: 10px; }
  .mapping-policy { text-align: left; }
  .mapping-card { padding: 16px; }
  .mapping-card-heading { align-items: flex-start; }
  .mapping-actions { gap: 8px; }
}
</style>
