import { createRouter, createWebHistory } from 'vue-router'
import { useAuth } from '@/stores/auth'
import LoginPage from '@/pages/LoginPage.vue'
import AdminLayout from '@/layouts/AdminLayout.vue'
import DashboardPage from '@/pages/DashboardPage.vue'
import AnalyticsPage from '@/pages/AnalyticsPage.vue'
import AccountsPage from '@/pages/AccountsPage.vue'
import ProxiesPage from '@/pages/ProxiesPage.vue'
import PluginsPage from '@/pages/PluginsPage.vue'
import TestPage from '@/pages/TestPage.vue'
import LogsPage from '@/pages/LogsPage.vue'
import TaskLogsPage from '@/pages/TaskLogsPage.vue'
import PluginLogsPage from '@/pages/PluginLogsPage.vue'
import PluginPage from '@/pages/PluginPage.vue'
import ConfigPage from '@/pages/ConfigPage.vue'
import ModelPlazaPage from '@/pages/ModelPlazaPage.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: LoginPage, meta: { public: true } },
    {
      path: '/',
      component: AdminLayout,
      meta: { requiresAuth: true },
      children: [
        { path: '', name: 'dashboard', component: DashboardPage },
        { path: 'analytics', name: 'analytics', component: AnalyticsPage },
        { path: 'models', name: 'models', component: ModelPlazaPage },
        { path: 'accounts', name: 'accounts', component: AccountsPage },
        { path: 'proxies', name: 'proxies', component: ProxiesPage },
        { path: 'plugins', name: 'plugins', component: PluginsPage },
        { path: 'plugins/:pluginKey', name: 'plugin-page', component: PluginPage },
        { path: 'test', name: 'test', component: TestPage },
        { path: 'logs', name: 'logs', component: LogsPage },
        { path: 'task-logs', name: 'task-logs', component: TaskLogsPage },
        { path: 'plugin-logs', name: 'plugin-logs', component: PluginLogsPage },
        { path: 'settings/config', name: 'config', component: ConfigPage },
      ],
    },
  ],
})

router.beforeEach(async (to) => {
  if (to.meta.public) return true
  const auth = useAuth()
  if (await auth.load()) return true
  return { name: 'login', query: { redirect: to.fullPath } }
})

export default router
