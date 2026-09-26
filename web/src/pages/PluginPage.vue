<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NCard, NSpin } from 'naive-ui'
import { api } from '@/services/api'

const route = useRoute(); const pluginKey = computed(() => String(route.params.pluginKey ?? '')); const frame = ref<HTMLIFrameElement | null>(null)
const query = useQuery({ queryKey: computed(() => ['plugin-page', pluginKey.value]), queryFn: () => api.pluginPage(pluginKey.value), enabled: computed(() => Boolean(pluginKey.value)) })
const srcdoc = computed(() => {
  const bridge = `<script>(function(){let n=0;const pending=new Map();window.addEventListener('message',e=>{if(e.data&&e.data.type==='router2api-response'){const p=pending.get(e.data.id);if(!p)return;pending.delete(e.data.id);e.data.ok?p.resolve(e.data.body):p.reject(new Error(e.data.error||'请求失败'))}});function call(type,payload){return new Promise(function(resolve,reject){const id=String(++n);pending.set(id,{resolve,reject});parent.postMessage(Object.assign({type:type,id:id},payload),'*')})}window.Router2API={request:function(method,route,body){return call('router2api-request',{method:method,route:route,body:body})},runTask:function(task){return call('router2api-task',{task:task})}}})();<\/script>`
  const html = query.data.value
  if (!html) return ''
  const viewport = /<meta\s+[^>]*name=["']viewport["']/i.test(html)
    ? ''
    : '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">'
  if (/<head\b[^>]*>/i.test(html))
    return html.replace(/<head\b[^>]*>/i, match => `${match}${viewport}${bridge}`)

  if (/<html\b[^>]*>/i.test(html))
    return html.replace(/<html\b[^>]*>/i, match => `${match}<head>${viewport}${bridge}</head>`)

  return `<!doctype html><html><head>${viewport}${bridge}</head><body>${html}</body></html>`
})
async function onMessage(event: MessageEvent) {
  if (event.source !== frame.value?.contentWindow) return
  if (event.data?.type === 'router2api-task') return runTask(event)
  if (event.data?.type !== 'router2api-request') return
  const route = String(event.data.route ?? '').replace(/^\/+/, '')
  if (!route || route.includes('..') || route.startsWith('api/admin') || route.includes('://')) return respond(event, event.data.id, false, null, '不允许访问该地址')
  const method = String(event.data.method ?? 'GET').toUpperCase()
  if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) return respond(event, event.data.id, false, null, '不允许的请求方法')
  try {
    const headers = new Headers()
    if (event.data.body !== undefined && event.data.body !== null) headers.set('Content-Type', 'application/json')
    const csrf = document.cookie.split('; ').find(item => item.startsWith('router_admin_csrf='))?.split('=').slice(1).join('=')
    if (csrf && method !== 'GET') headers.set('X-CSRF-Token', decodeURIComponent(csrf))
    const body = await fetch(`/api/plugins/${encodeURIComponent(pluginKey.value)}/${route}`, {
      method,
      credentials: 'include',
      headers,
      body: event.data.body === undefined || event.data.body === null ? undefined : JSON.stringify(event.data.body)
    })
    const text = await body.text(); let payload: unknown = text
    try { payload = text ? JSON.parse(text) : null } catch { /* HTML/text response */ }
    if (!body.ok) throw new Error((payload as { error?: string } | null)?.error || `请求失败（${body.status}）`)
    respond(event, event.data.id, true, payload)
  } catch (error) { respond(event, event.data.id, false, null, error instanceof Error ? error.message : '请求失败') }
}
async function runTask(event: MessageEvent) {
  const task = String(event.data.task ?? '')
  if (!task || task.includes('..') || task.includes('/') || task.includes('\\')) return respond(event, event.data.id, false, null, '不允许的任务名称')
  try {
    const csrf = document.cookie.split('; ').find(item => item.startsWith('router_admin_csrf='))?.split('=').slice(1).join('=')
    const headers = new Headers()
    if (csrf) headers.set('X-CSRF-Token', decodeURIComponent(csrf))
    const body = await fetch(`/api/admin/plugins/${encodeURIComponent(pluginKey.value)}/tasks/${encodeURIComponent(task)}/run`, { method: 'POST', credentials: 'include', headers })
    const text = await body.text(); let payload: unknown = text
    try { payload = text ? JSON.parse(text) : null } catch { /* empty/HTML response */ }
    if (!body.ok) throw new Error((payload as { error?: string } | null)?.error || `任务执行失败（${body.status}）`)
    respond(event, event.data.id, true, payload)
  } catch (error) { respond(event, event.data.id, false, null, error instanceof Error ? error.message : '任务执行失败') }
}
function respond(event: MessageEvent, id: string, ok: boolean, body: unknown, error?: string) { (event.source as WindowProxy | null)?.postMessage({ type: 'router2api-response', id, ok, body, error }, '*') }
onMounted(() => window.addEventListener('message', onMessage)); onBeforeUnmount(() => window.removeEventListener('message', onMessage))
</script>
<template>
  <NCard :bordered="false" class="plugin-page-card">
    <div class="plugin-page-content">
      <div v-if="query.isPending.value" class="plugin-page-state"><NSpin /></div>
      <div v-else-if="query.isError.value" class="plugin-page-state plugin-page-error"><NAlert type="error" title="插件页面加载失败">该插件可能没有可用主页面。</NAlert></div>
      <iframe v-else ref="frame" class="plugin-frame" sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox" :srcdoc="srcdoc" :title="`${pluginKey} 插件页面`" />
    </div>
  </NCard>
</template>
