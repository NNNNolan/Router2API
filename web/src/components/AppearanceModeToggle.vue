<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQueryClient } from '@tanstack/vue-query'
import { NButton, NIcon, useMessage } from 'naive-ui'
import { MoonOutline, SunnyOutline } from '@vicons/ionicons5'
import { api } from '@/services/api'
import { useAppearance } from '@/composables/useAppearance'

const { query, colorMode } = useAppearance()
const client = useQueryClient()
const message = useMessage()
const saving = ref(false)
const label = computed(() => colorMode.value === 'dark' ? '切换到日间模式' : '切换到黑夜模式')
async function toggle() {
  if (saving.value) return
  saving.value = true
  try {
    const saved = await api.saveConfig({ appearance: { colorMode: colorMode.value === 'dark' ? 'light' : 'dark' } })
    client.setQueryData(['appearance'], saved.appearance)
  } catch (error) {
    message.error(error instanceof Error ? error.message : '明暗模式切换失败')
  } finally { saving.value = false }
}
</script>

<template>
  <NButton quaternary circle :aria-label="label" :title="label" :aria-pressed="colorMode === 'dark'" :loading="saving" :disabled="query.isPending.value || query.isError.value" @click="toggle">
    <template #icon><NIcon :component="colorMode === 'dark' ? SunnyOutline : MoonOutline" /></template>
  </NButton>
</template>
