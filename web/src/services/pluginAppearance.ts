import type { AppearanceSettings } from './api'

export interface PluginAppearance extends AppearanceSettings {
  colors: { background: string; surface: string; text: string; muted: string; border: string }
}

// 此函数会序列化后在 sandbox iframe 内执行，不能引用模块作用域中的值。
export function installPluginAppearance() {
  const root = document.documentElement
  const sheet = document.createElement('style')
  sheet.textContent = ':root[data-router2api-color-mode="dark"] { color-scheme: dark; background-color: var(--router2api-background) !important; }'
  document.head.append(sheet)
  let appearance: PluginAppearance | undefined
  let scheduled = 0
  type Override = { property: string; original: string; priority: string; applied: string }
  const overrides = new Map<HTMLElement, Override[]>()

  function rgb(value: string) {
    const parts = value.match(/^rgba?\(([^)]+)\)$/)?.[1]?.split(/[,\s/]+/).map(Number)
    if (!parts || parts.length < 3 || (parts[3] ?? 1) === 0) return null
    const [r, g, b] = parts as [number, number, number]
    return { neutral: Math.max(r, g, b) - Math.min(r, g, b) <= 32, brightness: (r * 0.2126 + g * 0.7152 + b * 0.0722) / 255, alpha: parts[3] ?? 1 }
  }
  function restore() {
    for (const [element, values] of overrides) {
      for (const value of values) {
        // 插件主动写入的新样式属于插件，不能用旧快照覆盖。
        if (element.style.getPropertyValue(value.property) !== value.applied || element.style.getPropertyPriority(value.property) !== 'important') continue
        if (value.original) element.style.setProperty(value.property, value.original, value.priority)
        else element.style.removeProperty(value.property)
      }
    }
    overrides.clear()
  }
  function hasBrightColoredBackground(element: HTMLElement) {
    for (let parent: HTMLElement | null = element; parent; parent = parent.parentElement) {
      const color = rgb(getComputedStyle(parent).backgroundColor)
      if (color && color.alpha >= 0.5) return !color.neutral && color.brightness > 0.5
    }
    return false
  }
  function apply() {
    scheduled = 0
    observer.disconnect()
    restore()
    if (appearance) {
      root.dataset.router2apiColorMode = appearance.colorMode
      root.dataset.router2apiTheme = appearance.theme
      for (const [name, value] of Object.entries(appearance.colors)) root.style.setProperty(`--router2api-${name}`, value)
    }
    if (appearance?.colorMode === 'dark') {
      // 先读取原样式，再统一写入，避免父元素的覆盖影响子元素的颜色判断。
      const changes: { element: HTMLElement; values: { property: string; token: string }[] }[] = []
      for (const element of document.querySelectorAll<HTMLElement>('html, body, body *')) {
        if (!('style' in element) || element.closest('svg, canvas, picture, video, img, [role="img"]') || element.matches('script, style, link, meta')) continue
        const style = getComputedStyle(element)
        const values: { property: string; token: string }[] = []
        const background = rgb(style.backgroundColor)
        if (background?.neutral && background.alpha >= 0.5 && background.brightness > 0.5) {
          values.push({ property: 'background-color', token: element === document.body || element === root ? 'background' : 'surface' })
          if (style.backgroundImage !== 'none' && !style.backgroundImage.includes('url(')) values.push({ property: 'background-image', token: '' })
        }
        const text = rgb(style.color)
        if (text?.neutral && text.brightness < 0.6 && !hasBrightColoredBackground(element))
          values.push({ property: 'color', token: text.brightness < 0.3 && text.alpha >= 0.6 ? 'text' : 'muted' })
        for (const side of ['top', 'right', 'bottom', 'left']) {
          const color = rgb(style.getPropertyValue(`border-${side}-color`))
          if (color?.neutral && style.getPropertyValue(`border-${side}-style`) !== 'none') values.push({ property: `border-${side}-color`, token: 'border' })
        }
        if (values.length) changes.push({ element, values })
      }
      for (const { element, values } of changes) {
        overrides.set(element, values.map(({ property, token }) => {
          const value = { property, original: element.style.getPropertyValue(property), priority: element.style.getPropertyPriority(property), applied: '' }
          element.style.setProperty(property, token ? appearance!.colors[token as keyof PluginAppearance['colors']] : 'none', 'important')
          value.applied = element.style.getPropertyValue(property)
          return value
        }))
      }
    }
    observer.observe(root, { subtree: true, childList: true, attributes: true })
  }
  function schedule() {
    if (!scheduled) scheduled = requestAnimationFrame(apply)
  }
  // 页面整体扫描按帧合并；若插件包含上万节点，再改为只扫描发生变化的子树。
  const observer = new MutationObserver(schedule)
  window.addEventListener('message', event => {
    if (event.source !== parent || event.data?.type !== 'router2api-appearance') return
    const value = event.data.appearance as PluginAppearance | undefined
    if (!value || !['light', 'dark'].includes(value.colorMode) || !['minimal', 'default'].includes(value.theme)
      || !value.colors || ['background', 'surface', 'text', 'muted', 'border'].some(key => typeof value.colors[key as keyof typeof value.colors] !== 'string')) return
    appearance = value
    schedule()
  })
  document.addEventListener('DOMContentLoaded', schedule, { once: true })
  document.addEventListener('load', schedule, true)
  window.addEventListener('resize', schedule)
  parent.postMessage({ type: 'router2api-appearance-ready' }, '*')
}

export const pluginAppearanceScript = `<script>(${installPluginAppearance.toString()})();<\/script>`
