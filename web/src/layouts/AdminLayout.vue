<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useMessage, NAvatar, NButton, NDrawer, NDrawerContent, NIcon, NLayout, NLayoutContent, NLayoutHeader, NLayoutSider } from 'naive-ui'
import {
  AnalyticsOutline,
  BeakerOutline,
  BugOutline,
  CubeOutline,
  DocumentTextOutline,
  ExtensionPuzzleOutline,
  GitNetworkOutline,
  HomeOutline,
  LogOutOutline,
  MenuOutline,
  PeopleOutline,
  SettingsOutline,
} from '@vicons/ionicons5'
import { useAuth } from '@/stores/auth'
import { useQuery } from '@tanstack/vue-query'
import { api } from '@/services/api'
import AdminNavigation, { type NavItem, type NavSection } from '@/components/AdminNavigation.vue'

const route = useRoute()
const router = useRouter()
const message = useMessage()
const auth = useAuth()
const mobileMenuOpen = ref(false)
const animateNavigation = ref(true)
const currentUser = computed(() => auth.principal.value)

const pluginQuery = useQuery({ queryKey: ['plugins'], queryFn: api.plugins, staleTime: 30_000 })
const versionQuery = useQuery({ queryKey: ['host-version'], queryFn: api.hostVersion, staleTime: 60_000 })
const hostVersion = computed(() => versionQuery.data.value?.version
  ? `v${versionQuery.data.value.version}`
  : versionQuery.isError.value ? '版本获取失败' : '版本加载中…')
const coreNavItems: NavItem[] = [
  { to: '/', label: '总览', icon: HomeOutline },
  { to: '/analytics', label: '分析数据', icon: AnalyticsOutline },
  { to: '/models', label: '模型广场', icon: CubeOutline },
  { to: '/accounts', label: '账号管理', icon: PeopleOutline },
  { to: '/proxies', label: '代理池', icon: GitNetworkOutline },
  { to: '/plugins', label: '插件管理', icon: ExtensionPuzzleOutline },
  { to: '/test', label: '测试窗口', icon: BeakerOutline },
  { to: '/logs', label: '审计日志', icon: DocumentTextOutline },
  { to: '/task-logs', label: '任务日志', icon: DocumentTextOutline },
  { to: '/plugin-logs', label: '插件日志', icon: BugOutline },
  { to: '/settings/config', label: '配置中心', icon: SettingsOutline },
]
const navSections = computed<NavSection[]>(() => [
  { label: '工作台', items: coreNavItems.slice(0, 2) },
  { label: '资源管理', items: [
    ...coreNavItems.slice(2, 5),
    {
      ...coreNavItems[5],
      children: (pluginQuery.data.value ?? [])
        .filter(plugin => plugin.state === 'Active' && plugin.hasMainPage)
        .map(plugin => ({ to: `/plugins/${plugin.pluginKey}`, label: plugin.mainPageTitle || plugin.name, icon: ExtensionPuzzleOutline })),
    },
  ] },
  { label: '运行诊断', items: coreNavItems.slice(6, 10) },
  { label: '系统', items: coreNavItems.slice(10) },
])
const currentSection = computed(() => navSections.value.find(section => section.items.some(item =>
  item.to === route.path || item.children?.some(child => child.to === route.path)))?.label ?? '工作台')

function navigate(event: MouseEvent) {
  animateNavigation.value = event.detail > 0
  mobileMenuOpen.value = false
}

function resetPageScroll() {
  document.querySelector('.content-wrap .n-layout-scroll-container')?.scrollTo?.(0, 0)
}

const pageTitle = computed(() => navSections.value.flatMap(section => section.items)
  .flatMap(item => [item, ...item.children ?? []]).find(item => item.to === route.path)?.label ?? 'Router2API')

async function signOut() {
  try {
    await auth.logout()
    await router.push('/login')
  } catch {
    message.error('退出登录失败，请稍后重试')
  }
}

</script>

<template>
  <NLayout has-sider class="app-shell" :class="{ 'keyboard-navigation': !animateNavigation }" @keydown.capture="animateNavigation = false" @pointerdown.capture="animateNavigation = true">
    <NLayoutSider bordered :width="224" class="desktop-sidebar">
      <div class="brand-block">
        <div class="brand-mark"><NIcon :component="GitNetworkOutline" :size="22" /></div>
        <div>
          <div class="brand-name">Router2API</div>
          <div class="brand-caption">统一模型路由</div>
        </div>
      </div>
      <AdminNavigation id="desktop" :sections="navSections" label="主导航" @navigate="navigate" />
      <div class="sidebar-footer">
        <span class="sidebar-caption">管理员控制台</span>
        <span class="version-label" aria-label="宿主版本">{{ hostVersion }}</span>
      </div>
    </NLayoutSider>

    <NLayout class="workspace-layout">
      <NLayoutHeader bordered class="topbar">
        <div class="topbar-left">
          <NButton class="mobile-menu-button" quaternary circle aria-label="打开导航" @click="mobileMenuOpen = true">
            <template #icon><NIcon :component="MenuOutline" /></template>
          </NButton>
          <div class="page-breadcrumb">
            <span>{{ currentSection }}</span><span aria-hidden="true">/</span>
            <h1>{{ pageTitle }}</h1>
          </div>
        </div>
        <div class="topbar-actions">
          <div class="user-chip">
            <NAvatar round size="small" color="#DBEAFE" text-color="#1D4ED8">{{ currentUser?.userName?.slice(0, 1).toUpperCase() }}</NAvatar>
            <span>{{ currentUser?.userName }}</span>
          </div>
          <NButton quaternary circle aria-label="退出登录" @click="signOut">
            <template #icon><NIcon :component="LogOutOutline" /></template>
          </NButton>
        </div>
      </NLayoutHeader>

      <NLayoutContent class="content-wrap">
        <main id="main-content" class="page-content" tabindex="-1">
          <RouterView v-slot="{ Component, route: currentRoute }">
            <Transition name="page" mode="out-in" :css="animateNavigation" @before-enter="resetPageScroll">
              <div :key="currentRoute.path" class="route-view"><component :is="Component" /></div>
            </Transition>
          </RouterView>
        </main>
      </NLayoutContent>
    </NLayout>
  </NLayout>

  <NDrawer v-model:show="mobileMenuOpen" placement="left" :width="280">
    <NDrawerContent title="Router2API" closable :class="{ 'keyboard-navigation': !animateNavigation }" @keydown.capture="animateNavigation = false" @pointerdown.capture="animateNavigation = true">
      <AdminNavigation id="mobile" :sections="navSections" label="移动端主导航" @navigate="navigate" />
      <div class="sidebar-footer">
        <span class="version-label" aria-label="宿主版本">{{ hostVersion }}</span>
      </div>
    </NDrawerContent>
  </NDrawer>
</template>
