<script setup lang="ts">
import { onMounted, onUnmounted, ref, type Component } from 'vue'
import { useRoute } from 'vue-router'
import { NIcon } from 'naive-ui'
import { ChevronDownOutline } from '@vicons/ionicons5'

export type NavItem = { to: string; label: string; icon?: Component; children?: NavItem[] }
export type NavSection = { label: string; items: NavItem[]; to?: string }

defineProps<{ sections: NavSection[]; label: string; id: string }>()
const emit = defineEmits<{ navigate: [event: MouseEvent] }>()
const route = useRoute()
const navigation = ref<HTMLElement>()

function isActive(section: NavSection) {
  return section.items.some(item => item.to === route.path || item.children?.some(child => child.to === route.path))
}

function closeMenus(event?: MouseEvent) {
  for (const menu of navigation.value?.querySelectorAll('details') ?? []) {
    if (!event || !menu.contains(event.target as Node)) menu.open = false
  }
}

function navigate(event: MouseEvent) {
  closeMenus()
  emit('navigate', event)
}

function escapeMenu(event: KeyboardEvent) {
  const menu = (event.target as HTMLElement).closest('details')
  closeMenus()
  menu?.querySelector('summary')?.focus()
}

onMounted(() => document.addEventListener('click', closeMenus))
onUnmounted(() => document.removeEventListener('click', closeMenus))
</script>

<template>
  <nav ref="navigation" class="admin-nav" :aria-label="label" @keydown.esc="escapeMenu">
    <template v-for="section in sections" :key="section.label">
      <RouterLink v-if="section.to" :to="section.to" class="nav-top-link" :class="{ 'is-active': isActive(section) }" @click="navigate">{{ section.label }}</RouterLink>
      <details v-else class="nav-section" :class="{ 'is-active': isActive(section) }">
        <summary :aria-controls="`${id}-${section.label}`">{{ section.label }}<NIcon :component="ChevronDownOutline" :size="12" /></summary>
        <div :id="`${id}-${section.label}`" class="nav-menu">
          <template v-for="item in section.items" :key="item.to">
            <RouterLink :to="item.to" class="nav-link" @click="navigate">
              <NIcon v-if="item.icon" :component="item.icon" :size="17" /><span>{{ item.label }}</span>
            </RouterLink>
            <div v-if="item.children?.length" class="nav-subnav">
              <RouterLink v-for="child in item.children" :key="child.to" :to="child.to" class="nav-link nav-sublink" @click="navigate">
                <span class="nav-child-dot" /><span>{{ child.label }}</span>
              </RouterLink>
            </div>
          </template>
        </div>
      </details>
    </template>
  </nav>
</template>
