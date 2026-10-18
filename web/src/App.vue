<script setup lang="ts">
import { dateZhCN, NConfigProvider, NDialogProvider, NGlobalStyle, NMessageProvider, NNotificationProvider, zhCN } from 'naive-ui'
import type { GlobalThemeOverrides } from 'naive-ui'

const themeOverrides: GlobalThemeOverrides = {
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
  Button: { fontWeight: '500' },
  Card: { borderRadius: '10px', paddingMedium: '20px', titleFontSizeMedium: '15px', titleFontWeight: '600' },
  Input: { borderRadius: '8px' },
  DataTable: { thColor: '#F9FAFB', thFontWeight: '500', tdPaddingMedium: '12px 16px', borderColor: '#EEF0F3' },
  Statistic: { labelTextColor: '#667085', valueFontWeight: '600' },
}
</script>

<template>
  <NConfigProvider :locale="zhCN" :date-locale="dateZhCN" :theme-overrides="themeOverrides">
    <NGlobalStyle />
    <NMessageProvider>
      <NDialogProvider>
        <NNotificationProvider>
          <RouterView />
        </NNotificationProvider>
      </NDialogProvider>
    </NMessageProvider>
  </NConfigProvider>
</template>
