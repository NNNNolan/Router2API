import { computed, ref } from 'vue'
import { api, type Principal } from '@/services/api'

const principal = ref<Principal | null>(null)
const loading = ref(false)
let checked = false

export function useAuth() {
  const isAuthenticated = computed(() => principal.value !== null)

  async function load() {
    if (checked) return principal.value
    loading.value = true
    try {
      principal.value = await api.me()
      return principal.value
    } catch {
      principal.value = null
      return null
    } finally {
      checked = true
      loading.value = false
    }
  }

  async function login(username: string, password: string) {
    loading.value = true
    try {
      principal.value = await api.login(username, password)
      checked = true
      return principal.value
    } finally {
      loading.value = false
    }
  }

  async function logout() {
    await api.logout()
    principal.value = null
    checked = true
  }

  return { principal, loading, isAuthenticated, load, login, logout }
}
