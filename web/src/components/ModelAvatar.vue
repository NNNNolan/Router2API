<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { resolveModelIdentity } from '@/services/modelIdentity'

const props = defineProps<{ modelId: string; displayName?: string; platform?: string }>()
const identity = computed(() => resolveModelIdentity(props.modelId, props.displayName, props.platform))
const failed = ref(false)
watch(() => [props.modelId, props.displayName, props.platform], () => { failed.value = false })
</script>

<template>
  <span class="model-avatar" role="img" :aria-label="identity.name || '未知模型'" :title="identity.name">
    <img v-if="identity.icon && !failed" :src="identity.icon" alt="" width="30" height="30" loading="lazy" @error="failed = true">
    <span v-else class="model-initials">{{ identity.initials }}</span>
  </span>
</template>
