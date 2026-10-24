<script setup lang="ts">
import { computed, watch } from 'vue'
import { darkTheme, dateZhCN, NAlert, NButton, NConfigProvider, NDialogProvider, NGlobalStyle, NMessageProvider, NNotificationProvider, zhCN } from 'naive-ui'
import type { GlobalComponentConfig, GlobalThemeOverrides } from 'naive-ui'
import { useAppearance } from '@/composables/useAppearance'

const componentOptions: GlobalComponentConfig = { Button: { size: 'small' } }
const { query: appearance, theme, colorMode } = useAppearance()
watch([theme, colorMode], ([value, mode]) => {
  document.documentElement.dataset.theme = value
  document.documentElement.dataset.colorMode = mode
}, { immediate: true })

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
const minimalTheme: GlobalThemeOverrides = {
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
}
const themeOverrides = computed<GlobalThemeOverrides>(() => {
  const base = theme.value === 'minimal' ? minimalTheme : blueTheme
  if (colorMode.value === 'light') return base
  const primary = theme.value === 'minimal'
    ? ['#E4E4E7', '#FFFFFF', '#BFC2C9'] : ['#72A7FF', '#97BEFF', '#508FF0']
  return {
    ...base,
    common: {
      ...base.common,
      primaryColor: primary[0], primaryColorHover: primary[1], primaryColorPressed: primary[2],
      primaryColorSuppl: theme.value === 'minimal' ? '#71717A' : '#3B82F6',
      infoColor: primary[0], infoColorHover: primary[1], infoColorPressed: primary[2],
      bodyColor: '#101114', cardColor: '#18191D', modalColor: '#1E2025', popoverColor: '#202228',
      textColorBase: '#F2F4F7', textColor1: '#F9FAFB', textColor2: '#D0D5DD', textColor3: '#98A2B3',
      borderColor: '#343741', dividerColor: '#2B2E36',
      successColor: '#4ADE80', warningColor: '#FBBF24', errorColor: '#F87171',
    },
    Button: {
      ...base.Button,
      textColorPrimary: '#18181B', textColorHoverPrimary: '#18181B', textColorPressedPrimary: '#18181B', textColorFocusPrimary: '#18181B', textColorDisabledPrimary: '#18181B',
      textColorInfo: '#18181B', textColorHoverInfo: '#18181B', textColorPressedInfo: '#18181B', textColorFocusInfo: '#18181B', textColorDisabledInfo: '#18181B',
    },
    DataTable: { ...base.DataTable, thColor: '#202228', borderColor: '#343741' },
    Statistic: { ...base.Statistic, labelTextColor: '#A2A8B4' },
  }
})
</script>

<template>
  <NConfigProvider :theme="colorMode === 'dark' ? darkTheme : null" :locale="zhCN" :date-locale="dateZhCN" :component-options="componentOptions" :theme-overrides="themeOverrides">
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
