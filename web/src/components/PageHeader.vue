<script setup lang="ts">
defineProps<{
  eyebrow?: string
  title: string
  description: string
}>()
</script>

<template>
  <div class="page-heading">
    <div>
      <span v-if="eyebrow" class="page-eyebrow">{{ eyebrow }}</span>
      <h2>{{ title }}</h2>
      <p>{{ description }}</p>
    </div>
    <div class="page-actions"><slot name="actions" /></div>
  </div>
</template>
