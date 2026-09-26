<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NButton, NCard, NForm, NFormItem, NInput, NInputNumber, NSelect, NSpin, NSwitch, useMessage } from 'naive-ui'
import { api } from '@/services/api'

const message = useMessage()
const query = useQuery({ queryKey: ['config'], queryFn: api.config })
const saving = ref(false)
const refreshingProtocols = ref(false)
const protocolsUpdatedAt = ref('')
const form = reactive({
  adminEnabled: true,
  sessionLifetimeHours: 12,
  apiKeyEnabled: true,
  apiKey: '',
  databasePath: 'data/router2api.db',
  redisConnectionString: 'localhost:6379',
  redisInstanceName: 'router2api:',
  dataRetentionDays: 30,
  cleanupCron: '0 0 3 * * *',
  minimumPluginLogLevel: 'Information',
})

watch(() => query.data.value, value => {
  if (!value) return
  form.adminEnabled = value.auth.adminEnabled
  form.sessionLifetimeHours = value.auth.sessionLifetimeHours
  form.apiKeyEnabled = value.auth.apiKeyEnabled
  form.databasePath = value.database.path
  form.redisConnectionString = value.redis.connectionString
  form.redisInstanceName = value.redis.instanceName
  form.dataRetentionDays = value.logging.dataRetentionDays
  form.cleanupCron = value.logging.cleanupCron
  form.minimumPluginLogLevel = value.logging.minimumPluginLogLevel
}, { immediate: true })

async function save() {
  saving.value = true
  try {
    await api.saveConfig({
      auth: {
        enabled: form.adminEnabled,
        sessionLifetimeHours: form.sessionLifetimeHours,
        apiKey: { enabled: form.apiKeyEnabled, key: form.apiKey.trim() || undefined },
      },
      database: { path: form.databasePath.trim() },
      redis: { connectionString: form.redisConnectionString.trim(), instanceName: form.redisInstanceName },
      logging: { dataRetentionDays: form.dataRetentionDays, cleanupCron: form.cleanupCron.trim(), minimumPluginLogLevel: form.minimumPluginLogLevel },
    })
    form.apiKey = ''
    await query.refetch()
    message.success('配置已保存到 Config.json')
  } catch (error) {
    message.error(error instanceof Error ? error.message : '配置保存失败')
  } finally {
    saving.value = false
  }
}

async function refreshProtocols() {
  refreshingProtocols.value = true
  try {
    const result = await api.refreshModelMetadata()
    protocolsUpdatedAt.value = result.protocolsUpdatedAt
      ? new Date(result.protocolsUpdatedAt).toLocaleString('zh-CN', { hour12: false })
      : ''
    if (result.protocolsUpdatedAt) message.success(`模型协议已刷新，共 ${result.protocolCount} 条`)
    else if (result.protocolCount > 0) message.warning(`协议源暂不可用，继续使用缓存的 ${result.protocolCount} 条协议`)
    else message.warning('暂时没有获取到模型协议')
  } catch (error) {
    message.error(error instanceof Error ? error.message : '模型协议刷新失败')
  } finally {
    refreshingProtocols.value = false
  }
}
</script>

<template>
  <div class="config-page">
    <NAlert type="info" :bordered="false" class="config-source">
      <div class="config-source-line">
        <span>配置优先级：<strong>Config/Config.json</strong> → 用户机密 → <strong>ROUTER2API_</strong> 环境变量</span>
        <code>{{ query.data.value?.configPath || 'Config/Config.json' }}</code>
      </div>
    </NAlert>

    <NSpin v-if="query.isPending.value" class="center-spin" />
    <NAlert v-else-if="query.isError.value" type="error" title="配置读取失败">请刷新页面后重试。</NAlert>
    <NForm v-else label-placement="top" class="config-form">
      <div class="config-grid">
        <NCard title="访问控制" :bordered="false" class="settings-card config-card">
          <template #header-extra><span class="config-kicker">AUTH</span></template>
          <NFormItem label="管理员会话">
            <NSwitch v-model:value="form.adminEnabled"><template #checked>启用</template><template #unchecked>停用</template></NSwitch>
          </NFormItem>
          <NFormItem label="会话有效期（小时）">
            <NInputNumber v-model:value="form.sessionLifetimeHours" :min="1" :max="168" />
          </NFormItem>
          <NFormItem label="下游 API Key">
            <NSwitch v-model:value="form.apiKeyEnabled"><template #checked>启用</template><template #unchecked>停用</template></NSwitch>
          </NFormItem>
          <NFormItem label="替换 API Key">
            <NInput v-model:value="form.apiKey" type="password" show-password-on="click" :placeholder="query.data.value?.auth.apiKeyPreview || '留空保持当前 Key'" />
          </NFormItem>
          <p class="field-help">当前状态：{{ query.data.value?.auth.apiKeyConfigured ? '已配置' : '未配置' }}；管理员账户 {{ query.data.value?.auth.administratorCount ?? 0 }} 个。</p>
        </NCard>

        <NCard title="日志保留" :bordered="false" class="settings-card config-card">
          <template #header-extra><span class="config-kicker">RETENTION</span></template>
          <NFormItem label="数据日志保留天数">
            <NInputNumber v-model:value="form.dataRetentionDays" :min="1" :max="3650" />
          </NFormItem>
          <NFormItem label="清理 Cron（UTC）">
            <NInput v-model:value="form.cleanupCron" placeholder="0 0 3 * * *" />
          </NFormItem>
          <NFormItem label="插件日志最低入库等级">
            <NSelect v-model:value="form.minimumPluginLogLevel" :options="[{ label: 'Debug（详细）', value: 'Debug' }, { label: 'Information（常规）', value: 'Information' }, { label: 'Warning（警告及错误）', value: 'Warning' }, { label: 'Error（仅错误）', value: 'Error' }]" />
          </NFormItem>
          <p class="field-help">会清理请求、插件、任务、资源审计和用量日志；默认每天 UTC 03:00 执行。</p>
        </NCard>

        <NCard title="数据库" :bordered="false" class="settings-card config-card">
          <template #header-extra><span class="config-kicker">SQLITE</span></template>
          <NFormItem label="SQLite 文件路径">
            <NInput v-model:value="form.databasePath" />
          </NFormItem>
          <p class="field-help">修改数据库或 Redis 后需要重启宿主才会切换连接。</p>
        </NCard>

        <NCard title="Redis 运行态" :bordered="false" class="settings-card config-card">
          <template #header-extra><span class="config-kicker">REDIS</span></template>
          <NFormItem label="连接字符串">
            <NInput v-model:value="form.redisConnectionString" />
          </NFormItem>
          <NFormItem label="实例前缀">
            <NInput v-model:value="form.redisInstanceName" />
          </NFormItem>
          <p class="field-help">Redis 保存代理策略、账号冷却、插件短期缓存和任务锁。</p>
        </NCard>

        <NCard title="模型协议目录" :bordered="false" class="settings-card config-card">
          <template #header-extra><span class="config-kicker">PROTOCOLS</span></template>
          <p class="field-help">宿主启动时自动加载，每 30 分钟更新一次；也可以在这里立即获取并刷新协议。</p>
          <NButton attr-type="button" secondary type="primary" :loading="refreshingProtocols" @click="refreshProtocols">获取并刷新模型协议</NButton>
          <p v-if="protocolsUpdatedAt" class="field-help">最近刷新：{{ protocolsUpdatedAt }}</p>
        </NCard>
      </div>
      <div class="config-actions">
        <span>保存后会立即更新运行态配置；数据库和 Redis 的修改在重启后生效。</span>
        <NButton type="primary" :loading="saving" @click="save">保存配置</NButton>
      </div>
    </NForm>
  </div>
</template>
