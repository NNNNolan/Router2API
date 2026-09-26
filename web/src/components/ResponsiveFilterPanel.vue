<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

withDefaults(defineProps<{
  title?: string
  variant?: 'table' | 'plain'
}>(), {
  title: '筛选条件',
  variant: 'table',
})

const isMobile = ref(false)
const expanded = ref(false)
let mediaQuery: MediaQueryList | undefined

function updateViewport(event?: MediaQueryListEvent) {
  const nextIsMobile = event?.matches ?? mediaQuery?.matches ?? false
  if (nextIsMobile !== isMobile.value) {
    isMobile.value = nextIsMobile
    expanded.value = !nextIsMobile
  }
}

onMounted(() => {
  mediaQuery = window.matchMedia('(max-width: 800px)')
  updateViewport()
  mediaQuery.addEventListener('change', updateViewport)
})

onBeforeUnmount(() => mediaQuery?.removeEventListener('change', updateViewport))
</script>

<template>
  <div :class="['responsive-filter-panel', variant === 'table' && 'table-filter-panel', { 'is-mobile': isMobile, 'is-expanded': expanded }]">
    <button
      v-if="isMobile"
      class="responsive-filter-toggle"
      type="button"
      :aria-expanded="expanded"
      @click="expanded = !expanded"
    >
      <span>{{ title }}</span>
      <span class="responsive-filter-toggle-state">{{ expanded ? '收起' : '展开' }}</span>
    </button>
    <div v-show="!isMobile || expanded" class="responsive-filter-content">
      <slot />
    </div>
  </div>
</template>
