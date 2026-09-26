<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useMessage, NButton, NCard, NCheckbox, NForm, NFormItem, NIcon, NInput } from 'naive-ui'
import { ArrowForwardOutline, LockClosedOutline } from '@vicons/ionicons5'
import { ApiError } from '@/services/api'
import { useAuth } from '@/stores/auth'

const router = useRouter()
const route = useRoute()
const message = useMessage()
const auth = useAuth()
const rememberedUsernameKey = 'router2api.remembered-username'
const rememberedPasswordKey = 'router2api.remembered-password'
function readRememberedCredentials() {
  try {
    return {
      username: localStorage.getItem(rememberedUsernameKey) ?? '',
      password: localStorage.getItem(rememberedPasswordKey) ?? '',
    }
  } catch {
    return { username: '', password: '' }
  }
}
const rememberedCredentials = readRememberedCredentials()
const username = ref(rememberedCredentials.username)
const password = ref(rememberedCredentials.password)
const rememberAccount = ref(Boolean(username.value || password.value))
const submitting = ref(false)

function clearRememberedCredentials() {
  try {
    localStorage.removeItem(rememberedUsernameKey)
    localStorage.removeItem(rememberedPasswordKey)
  } catch {
    // Login should still work when browser storage is unavailable.
  }
}
function setRememberAccount(checked: boolean) {
  rememberAccount.value = checked
  if (!checked) clearRememberedCredentials()
}

async function submit() {
  if (!username.value.trim() || !password.value) {
    message.warning('请输入用户名和密码')
    return
  }
  const account = username.value.trim()
  try {
    if (rememberAccount.value) {
      localStorage.setItem(rememberedUsernameKey, account)
      localStorage.setItem(rememberedPasswordKey, password.value)
    } else {
      clearRememberedCredentials()
    }
  } catch {
    clearRememberedCredentials()
    // Login should still work when browser storage is unavailable.
  }
  submitting.value = true
  try {
    await auth.login(account, password.value)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    await router.replace(redirect)
  } catch (error) {
    message.error(error instanceof ApiError ? error.message : '登录失败，请检查服务状态')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="login-page">
    <div class="login-decoration decoration-one" />
    <div class="login-decoration decoration-two" />
    <section class="login-panel" aria-labelledby="login-title">
      <div class="login-brand">
        <div class="brand-mark large">R</div>
        <span>Router2API</span>
      </div>
      <NCard class="login-card" :bordered="false">
        <div class="login-heading">
          <div class="eyebrow"><NIcon :component="LockClosedOutline" /> 管理员入口</div>
          <h1 id="login-title">欢迎回来</h1>
          <p>登录控制台，管理你的模型路由与代理资源。</p>
        </div>
        <NForm @submit.prevent="submit">
          <NFormItem label="用户名">
            <NInput v-model:value="username" size="large" placeholder="输入管理员用户名" autocomplete="username" />
          </NFormItem>
          <NFormItem label="密码">
            <NInput v-model:value="password" size="large" type="password" show-password-on="click" placeholder="输入密码" autocomplete="current-password" @keyup.enter="submit" />
          </NFormItem>
          <div class="login-remember"><NCheckbox :checked="rememberAccount" @update:checked="setRememberAccount">记住账号和密码</NCheckbox></div>
          <NButton type="primary" size="large" block :loading="submitting" attr-type="submit">
            登录控制台
            <template #icon><NIcon :component="ArrowForwardOutline" /></template>
          </NButton>
        </NForm>
      </NCard>
      <p class="login-footer">Router2API · 统一模型接入层</p>
    </section>
  </main>
</template>
