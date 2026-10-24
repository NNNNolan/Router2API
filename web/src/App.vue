<script setup lang="ts">
import { computed, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { dateZhCN, NAlert, NButton, NConfigProvider, NDialogProvider, NGlobalStyle, NMessageProvider, NNotificationProvider, zhCN } from 'naive-ui'
import type { GlobalComponentConfig, GlobalThemeOverrides } from 'naive-ui'
import { api } from '@/services/api'

const componentOptions: GlobalComponentConfig = { Button: { size: 'small' } }
const appearance = useQuery({ queryKey: ['appearance'], queryFn: api.appearance, staleTime: Infinity, refetchOnWindowFocus: false })
const theme = computed(() => appearance.data.value?.theme ?? 'minimal')
watch(theme, value => { document.documentElement.dataset.theme = value }, { immediate: true })

const blueTheme: GlobalThemeOverrides = {
  common: {
    primaryColor: '#2563EB',
    primaryColorHover: '#3B82F6',
    primaryColorPressed: '#1D4ED8',
    infoColor: '#2563EB',
    successColor: '#16A34A',
    warningColor: '#D97706',
    errorColor: '#DC2626',
    borderRadius: '8px',
    bodyColor: '#F6F7F9',
    textColorBase: '#182230',
    borderColor: '#E4E7EC',
    fontFamily: 'Inter, "PingFang SC", "Microsoft YaHei", sans-serif',
  },
  Button: { fontWeight: '500', fontSizeSmall: '12px', paddingSmall: '0 8px', paddingRoundSmall: '0 10px', iconSizeSmall: '16px' },
  Card: { borderRadius: '16px', paddingMedium: '20px', titleFontSizeMedium: '15px', titleFontWeight: '600' },
  Input: { borderRadius: '8px' },
  DataTable: { thColor: '#F9FAFB', thFontWeight: '500', tdPaddingMedium: '12px 16px', borderColor: '#EEF0F3' },
  Statistic: { labelTextColor: '#667085', valueFontWeight: '600' },
}
const themeOverrides = computed<GlobalThemeOverrides>(() => theme.value === 'minimal' ? {
  ...blueTheme,
  common: {
    ...blueTheme.common,
    primaryColor: '#18181B', primaryColorHover: '#3F3F46', primaryColorPressed: '#09090B',
    infoColor: '#18181B', infoColorHover: '#3F3F46', infoColorPressed: '#09090B',
    bodyColor: '#FFFFFF', cardColor: '#FFFFFF', modalColor: '#FFFFFF', popoverColor: '#FFFFFF',
    textColorBase: '#18181B', textColor1: '#18181B', textColor2: '#3F3F46', textColor3: '#71717A',
    borderColor: '#E4E4E7', dividerColor: '#E4E4E7', borderRadius: '6px',
  },
  Card: { ...blueTheme.Card, borderRadius: '10px' },
  Input: { ...blueTheme.Input, borderRadius: '6px' },
  DataTable: { ...blueTheme.DataTable, thColor: '#FAFAFA', borderColor: '#E4E4E7' },
  Statistic: { ...blueTheme.Statistic, labelTextColor: '#71717A' },
} : blueTheme)
</script>

<template>
  <NConfigProvider :locale="zhCN" :date-locale="dateZhCN" :component-options="componentOptions" :theme-overrides="themeOverrides">
    <NGlobalStyle />
    <NAlert v-if="appearance.isError.value" type="warning" title="主题设置加载失败" class="appearance-error">
      请重试读取已保存的主题。<NButton text @click="appearance.refetch()">重试</NButton>
    </NAlert>
    <NMessageProvider>
      <NDialogProvider>
        <NNotificationProvider>
          <RouterView />
        </NNotificationProvider>
      </NDialogProvider>
    </NMessageProvider>
  </NConfigProvider>
</template>
