import { computed } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { api } from '@/services/api'

export function useAppearance() {
  const query = useQuery({ queryKey: ['appearance'], queryFn: api.appearance, staleTime: Infinity, refetchOnWindowFocus: false })
  const theme = computed(() => query.data.value?.theme ?? 'minimal')
  const colorMode = computed(() => query.data.value?.colorMode ?? 'light')
  return { query, theme, colorMode }
}
