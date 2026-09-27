<script setup lang="ts">
import { computed, ref, type Component } from 'vue'
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

const route = useRoute()
const router = useRouter()
const message = useMessage()
const auth = useAuth()
const mobileMenuOpen = ref(false)
const currentUser = computed(() => auth.principal.value)

type NavItem = { to: string; label: string; icon: Component; children?: NavItem[] }

const pluginQuery = useQuery({ queryKey: ['plugins'], queryFn: api.plugins, staleTime: 30_000 })
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
const navItems = computed(() => [
  ...coreNavItems.slice(0, 5),
  {
    ...coreNavItems[5],
    children: (pluginQuery.data.value ?? [])
      .filter(plugin => plugin.state === 'Active' && plugin.hasMainPage)
      .map(plugin => ({ to: `/plugins/${plugin.pluginKey}`, label: plugin.mainPageTitle || plugin.name, icon: ExtensionPuzzleOutline })),
  },
  ...coreNavItems.slice(6),
])

const titles: Record<string, string> = {
  '/': '总览',
  '/analytics': '分析数据',
  '/models': '模型广场',
  '/accounts': '账号管理',
  '/proxies': '代理池',
  '/plugins': '插件管理',
  '/test': '测试窗口',
  '/logs': '审计日志',
  '/task-logs': '任务日志',
  '/plugin-logs': '插件日志',
  '/settings/config': '配置中心',
}
const pageTitle = computed(() => titles[route.path] ?? pluginQuery.data.value?.find(plugin => `/plugins/${plugin.pluginKey}` === route.path)?.mainPageTitle ?? 'Router2API')

async function signOut() {
  try {
    await auth.logout()
    await router.push('/login')
  } catch {
    message.error('退出登录失败，请稍后重试')
  }
}

function closeMobileMenu() {
  mobileMenuOpen.value = false
}
</script>

<template>
  <NLayout has-sider class="app-shell">
    <NLayoutSider bordered :width="248" class="desktop-sidebar">
      <div class="brand-block">
        <div class="brand-mark">R</div>
        <div>
          <div class="brand-name">Router2API</div>
          <div class="brand-caption">统一模型路由</div>
        </div>
      </div>
      <nav class="side-nav" aria-label="主导航">
        <div v-for="item in navItems" :key="item.to" class="nav-group">
          <RouterLink :to="item.to" class="nav-link">
            <NIcon :component="item.icon" :size="19" />
            <span>{{ item.label }}</span>
          </RouterLink>
          <div v-if="item.children?.length" class="nav-subnav">
            <RouterLink v-for="child in item.children" :key="child.to" :to="child.to" class="nav-link nav-sublink">
              <NIcon :component="child.icon" :size="16" />
              <span>{{ child.label }}</span>
            </RouterLink>
          </div>
        </div>
      </nav>
      <div class="sidebar-footer">
        <div class="health-dot"><span />服务运行中</div>
        <span class="version-label">MVP · v0.1</span>
      </div>
    </NLayoutSider>

    <NLayout>
      <NLayoutHeader bordered class="topbar">
        <div class="topbar-left">
          <NButton class="mobile-menu-button" quaternary circle aria-label="打开导航" @click="mobileMenuOpen = true">
            <template #icon><NIcon :component="MenuOutline" /></template>
          </NButton>
          <div>
            <h1>{{ pageTitle }}</h1>
            <p>稳定地连接账号、代理与模型能力</p>
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
          <RouterView />
        </main>
      </NLayoutContent>
    </NLayout>
  </NLayout>

  <NDrawer v-model:show="mobileMenuOpen" placement="left" :width="280">
    <NDrawerContent title="Router2API" closable>
      <nav class="mobile-nav" aria-label="移动端主导航">
        <div v-for="item in navItems" :key="item.to" class="nav-group">
          <RouterLink :to="item.to" class="nav-link" @click="closeMobileMenu">
            <NIcon :component="item.icon" :size="19" />
            <span>{{ item.label }}</span>
          </RouterLink>
          <div v-if="item.children?.length" class="nav-subnav">
            <RouterLink v-for="child in item.children" :key="child.to" :to="child.to" class="nav-link nav-sublink" @click="closeMobileMenu">
              <NIcon :component="child.icon" :size="16" />
              <span>{{ child.label }}</span>
            </RouterLink>
          </div>
        </div>
      </nav>
    </NDrawerContent>
  </NDrawer>
</template>
