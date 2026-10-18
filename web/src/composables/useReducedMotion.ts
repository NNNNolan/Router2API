import { onBeforeUnmount, onMounted, ref } from 'vue'

// 图表绘制在 Canvas 上，需要把系统偏好显式传给图表，CSS 无法关闭它的动画。
export function useReducedMotion() {
  const reducedMotion = ref(true)
  let mediaQuery: MediaQueryList | undefined
  const update = () => { reducedMotion.value = mediaQuery!.matches }

  onMounted(() => {
    mediaQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
    update()
    mediaQuery.addEventListener('change', update)
  })
  onBeforeUnmount(() => mediaQuery?.removeEventListener('change', update))
  return reducedMotion
}
