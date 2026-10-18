<script setup lang="ts">
import { ref, type Component } from 'vue'
import { NIcon } from 'naive-ui'
import { ChevronDownOutline } from '@vicons/ionicons5'

export type NavItem = { to: string; label: string; icon: Component; children?: NavItem[] }
export type NavSection = { label: string; items: NavItem[] }

defineProps<{ sections: NavSection[]; label: string; id: string }>()
defineEmits<{ navigate: [event: MouseEvent] }>()

const pluginsExpanded = ref(true)
const animate = ref(true)

function togglePlugins(event: MouseEvent) {
  animate.value = event.detail > 0
  pluginsExpanded.value = !pluginsExpanded.value
}
</script>

<template>
  <nav class="side-nav" :class="{ 'keyboard-navigation': !animate }" :aria-label="label" @keydown.capture="animate = false" @pointerdown.capture="animate = true">
    <section v-for="section in sections" :key="section.label" class="nav-section" :aria-label="section.label">
      <div class="nav-section-label">{{ section.label }}</div>
      <div v-for="item in section.items" :key="item.to" class="nav-group">
        <div class="nav-item">
          <RouterLink :to="item.to" class="nav-link" @click="$emit('navigate', $event)">
            <NIcon :component="item.icon" :size="18" />
            <span>{{ item.label }}</span>
          </RouterLink>
          <button v-if="item.children?.length" class="nav-expand" type="button" :aria-label="`${pluginsExpanded ? '收起' : '展开'}插件页面`" :aria-expanded="pluginsExpanded" :aria-controls="`${id}-plugins`" @click="togglePlugins">
            <NIcon :component="ChevronDownOutline" :size="14" :class="{ 'is-collapsed': !pluginsExpanded }" />
          </button>
        </div>
        <Transition name="panel" :css="animate">
          <div v-if="item.children?.length" v-show="pluginsExpanded" :id="`${id}-plugins`" class="nav-subnav">
            <RouterLink v-for="child in item.children" :key="child.to" :to="child.to" class="nav-link nav-sublink" @click="$emit('navigate', $event)">
              <span class="nav-child-dot" />
              <span>{{ child.label }}</span>
            </RouterLink>
          </div>
        </Transition>
      </div>
    </section>
  </nav>
</template>
