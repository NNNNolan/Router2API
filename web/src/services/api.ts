export interface Principal { userName: string; role: string }

export interface Metrics {
  totalRequests: number
  successfulRequests: number
  failedRequests: number
  averageLatencyMs: number
  requestsPerMinute: number
  requestsToday: number
  activeRequests: number
  platforms: PlatformMetric[]
}

export interface PlatformMetric { platform: string; requests: number; errors: number; averageLatencyMs: number }
export interface Platform { name: string; pluginKey: string; displayName: string; probeEndpoint: string | null; enabled: boolean }

export interface PageResult<T> { items: T[]; page: number; pageSize: number; total: number; totalPages: number }
export type PageParams = { page?: number; pageSize?: 20 | 50 | 100 }

export interface Account {
  id: string
  pluginKey: string
  platform: string
  label: string | null
  enabled: boolean
  credentialKind: string
  credential: Record<string, unknown>
  state?: string
  expiresAt?: string | null
}

export interface ProxyEndpoint {
  id: string
  subscriptionId: string
  scheme: string
  host: string
  port: number
  enabled: boolean
  state: string
  latencyMs: number
  averageLatencyMs: number
  averageSpeedBytesPerSecond: number
  compositeScore: number
  lastProbeAt: string | null
  probeStatus: string
  probeError?: string | null
  probeConfigurationVersion: number
}

export interface ProxyProbeResult {
  success: boolean
  latencyMs: number
  speedBytesPerSecond: number
  status: string
  error?: string | null
}

export interface ProxySubscription {
  id: string
  name: string
  url: string
  scheme: string
  enabled: boolean
  refreshIntervalMinutes: number
  lastRefreshAt: string | null
  endpointCount: number
  lastError?: string | null
}

export interface PluginTask { name: string; cron: string; description: string | null }
export interface PluginDescriptor {
  pluginKey: string
  name: string
  version: string
  description: string | null
  runtime: string
  state: string
  directoryPath: string
  loadedAt: string
  inFlight: number
  hasMainPage: boolean
  mainPageTitle: string | null
  mainPageVersion: string | null
  tasks: PluginTask[]
  routes: string[]
}

export interface PluginRepository { owner: string; repo: string }
export interface PluginReleaseSummary { tag: string; publishedAt: string | null }
export interface PluginReleaseEntry { id: string; name: string; description: string; runtime: string; version: string; asset: string; sha256: string; contentSha256?: string | null; sizeBytes: number }
export interface PluginReleaseIndex { schemaVersion: number; tag: string; plugins: PluginReleaseEntry[] }
export interface PluginInstallation { pluginId: string; owner: string; repo: string; tag: string; sha256: string; contentSha256?: string | null; description: string }
export interface PluginAvailableUpdate { installed: PluginInstallation; available: PluginReleaseEntry; tag: string }

export interface ModelDescriptor { id: string; displayName: string; platform?: string; contextWindow?: number; inputLimit?: number; outputLimit?: number; supportsStreaming?: boolean; supportsEmbeddings?: boolean; supportsReasoning?: boolean; reasoningLevels?: string[]; reasoningTokenLimit?: number | null }
export interface ModelPlazaModel {
  id: string
  platform: string
  platformName: string
  displayName: string
  contextWindow: number
  inputLimit: number
  outputLimit: number
  supportsReasoning: boolean
  reasoningLevels: string[]
  reasoningTokenLimit: number | null
}
export interface ModelPlazaResponse { updatedAt: string; source: string; models: ModelPlazaModel[] }
export interface RequestAttemptDetail { proxyId: string | null; proxyAddress: string; statusCode: number | null; outcome: string; isTransportFailure: boolean; reason: string | null; durationMs: number }
export interface RequestLog { id: string; traceId: string; platform: string | null; model: string | null; success: boolean; statusCode: number; durationMs: number; ttfbMs: number | null; usage: { promptTokens: number; completionTokens: number; totalTokens: number } | null; attemptDetails?: RequestAttemptDetail[]; createdAt: string }
export interface TaskLog { id: string; pluginKey: string; platform: string | null; taskName: string; accountId: string | null; status: string; message: string | null; error: string | null; detailsJson: string | null; durationMs: number; startedAt: string; finishedAt: string | null }
export interface PluginLog { id: string; pluginKey: string; platform: string | null; level: string; eventType: string; message: string; traceId: string | null; taskName: string | null; accountId: string | null; model: string | null; statusCode: number | null; durationMs: number | null; detailsJson: string | null; createdAt: string }
export interface ConfigSnapshot {
  configPath: string
  fileExists: boolean
  source: string
  auth: { adminEnabled: boolean; sessionLifetimeHours: number; administratorCount: number; apiKeyEnabled: boolean; apiKeyPreview: string; apiKeyConfigured: boolean }
  database: { path: string }
  redis: { connectionString: string; instanceName: string }
  logging: { dataRetentionDays: number; cleanupCron: string; minimumPluginLogLevel: string }
}
export interface AnalyticsBucket { startUtc: string; requests: number; successes: number; failures: number; tokens: number; averageLatencyMs: number; p95LatencyMs: number }
export interface AnalyticsReport { fromUtc: string; toUtc: string; requests: number; successes: number; failures: number; tokens: number; averageLatencyMs: number; p95LatencyMs: number; buckets: AnalyticsBucket[]; byPlatform: Record<string, number> }

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) { super(message); this.name = 'ApiError' }
}

type ApiRequestInit = Omit<RequestInit, 'body'> & { body?: unknown }
type JsonObject = Record<string, unknown>

function asObject(value: unknown): JsonObject { return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as JsonObject : {} }
function pick(object: JsonObject, ...keys: string[]): unknown { for (const key of keys) if (object[key] !== undefined && object[key] !== null) return object[key]; return undefined }
function asString(value: unknown): string | null { return typeof value === 'string' ? value : value == null ? null : String(value) }
function asNumber(value: unknown): number | undefined { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) ? number : undefined }
function asBoolean(value: unknown): boolean | undefined { if (typeof value === 'boolean') return value; if (typeof value === 'number') return value !== 0; if (typeof value === 'string') return /^(true|active|enabled|on|1)$/i.test(value) ? true : /^(false|inactive|disabled|off|0)$/i.test(value) ? false : undefined; return undefined }
function asArray(value: unknown): unknown[] { if (Array.isArray(value)) return value; const object = asObject(value); for (const key of ['data', 'items', 'results', 'records']) if (Array.isArray(object[key])) return object[key] as unknown[]; return [] }
function enumName(value: unknown): string | null { if (typeof value === 'number') return ['Active', 'Cooling', 'Draining', 'Invalid', 'Disabled', 'Removed'][value] ?? null; return asString(value) }
function isActive(value: unknown): boolean { const state = enumName(value); return state === null || /^(active|enabled|on)$/i.test(state) }

function normalizePrincipal(value: unknown): Principal {
  const raw = asObject(value)
  const roles = asArray(pick(raw, 'roles')).map(asString).filter((role): role is string => role !== null)
  return { userName: asString(pick(raw, 'userName', 'username', 'user', 'displayName')) ?? '', role: asString(pick(raw, 'role')) ?? roles[0] ?? '' }
}

function normalizeAccount(value: unknown): Account {
  const raw = asObject(value); const status = asObject(pick(raw, 'status')); const state = enumName(pick(raw, 'state')) ?? enumName(pick(status, 'state'))
  return { id: asString(pick(raw, 'id', 'accountId')) ?? '', pluginKey: asString(pick(raw, 'pluginKey')) ?? '', platform: asString(pick(raw, 'platform', 'platformName')) ?? '', label: asString(pick(raw, 'label', 'displayName', 'name')), enabled: asBoolean(pick(raw, 'enabled')) ?? isActive(state), credentialKind: asString(pick(raw, 'credentialKind', 'credential_kind')) ?? 'custom', credential: asObject(pick(raw, 'credential', 'credentials')), state: state ?? undefined, expiresAt: asString(pick(raw, 'expiresAt', 'expires_at')) }
}

function normalizeProxy(value: unknown): ProxyEndpoint {
  const raw = asObject(value); const status = asObject(pick(raw, 'status')); const state = enumName(pick(raw, 'state')) ?? enumName(pick(status, 'state')) ?? 'Active'
  return { id: asString(pick(raw, 'id')) ?? '', subscriptionId: asString(pick(raw, 'subscriptionId', 'subscription_id')) ?? '', scheme: asString(pick(raw, 'scheme')) ?? 'http', host: asString(pick(raw, 'host')) ?? '', port: asNumber(pick(raw, 'port')) ?? 0, enabled: asBoolean(pick(raw, 'enabled')) ?? isActive(state), state, latencyMs: asNumber(pick(raw, 'latencyMs')) ?? 0, averageLatencyMs: asNumber(pick(raw, 'averageLatencyMs', 'avgLatencyMs')) ?? 0, averageSpeedBytesPerSecond: asNumber(pick(raw, 'averageSpeedBytesPerSecond')) ?? 0, compositeScore: asNumber(pick(raw, 'compositeScore')) ?? 0, lastProbeAt: asString(pick(raw, 'lastProbeAt', 'lastProbedAt')), probeStatus: asString(pick(raw, 'probeStatus')) ?? 'Unknown', probeConfigurationVersion: asNumber(pick(raw, 'probeConfigurationVersion')) ?? 0 }
}

function normalizeSubscription(value: unknown): ProxySubscription {
  const raw = asObject(value)
  return { id: asString(pick(raw, 'id')) ?? '', name: asString(pick(raw, 'name')) ?? '', url: asString(pick(raw, 'url')) ?? '', scheme: asString(pick(raw, 'scheme')) ?? 'http', enabled: asBoolean(pick(raw, 'enabled')) ?? true, refreshIntervalMinutes: asNumber(pick(raw, 'refreshIntervalMinutes')) ?? 60, lastRefreshAt: asString(pick(raw, 'lastRefreshAt', 'lastFetchedAt')), endpointCount: asNumber(pick(raw, 'endpointCount', 'lastFetchedCount')) ?? 0, lastError: asString(pick(raw, 'lastError')) }
}

function normalizePlugin(value: unknown): PluginDescriptor {
  const raw = asObject(value)
  const tasks = asArray(pick(raw, 'tasks')).map(item => { const task = asObject(item); return { name: asString(pick(task, 'name')) ?? '', cron: asString(pick(task, 'cron')) ?? '', description: asString(pick(task, 'description')) } })
  return { pluginKey: asString(pick(raw, 'pluginKey', 'name')) ?? '', name: asString(pick(raw, 'name', 'pluginKey')) ?? '', version: asString(pick(raw, 'version')) ?? '', description: asString(pick(raw, 'description')), runtime: asString(pick(raw, 'runtime')) ?? 'dotnet', state: asString(pick(raw, 'state')) ?? '', directoryPath: asString(pick(raw, 'directoryPath')) ?? '', loadedAt: asString(pick(raw, 'loadedAt')) ?? '', inFlight: asNumber(pick(raw, 'inFlight')) ?? 0, hasMainPage: asBoolean(pick(raw, 'hasMainPage')) ?? false, mainPageTitle: asString(pick(raw, 'mainPageTitle')), mainPageVersion: asString(pick(raw, 'mainPageVersion')), tasks, routes: asArray(pick(raw, 'routes')).map(asString).filter((route): route is string => route !== null) }
}

function normalizeModelPlaza(value: unknown): ModelPlazaResponse {
  const raw = asObject(value)
  const models = asArray(pick(raw, 'models', 'data')).map(item => {
    const model = asObject(item)
    return {
      id: asString(pick(model, 'id')) ?? '',
      platform: asString(pick(model, 'platform')) ?? '',
      platformName: asString(pick(model, 'platformName', 'platform_name', 'platform')) ?? '',
      displayName: asString(pick(model, 'displayName', 'name', 'id')) ?? '',
      contextWindow: asNumber(pick(model, 'contextWindow', 'context_window')) ?? 0,
      inputLimit: asNumber(pick(model, 'inputLimit', 'input_limit')) ?? 0,
      outputLimit: asNumber(pick(model, 'outputLimit', 'output_limit')) ?? 0,
      supportsReasoning: asBoolean(pick(model, 'supportsReasoning', 'reasoning')) ?? false,
      reasoningLevels: asArray(pick(model, 'reasoningLevels', 'reasoning_levels')).map(asString).filter((level): level is string => level !== null),
      reasoningTokenLimit: asNumber(pick(model, 'reasoningTokenLimit', 'reasoning_token_limit')) ?? null,
    }
  }).filter(model => model.id.length > 0)
  return { updatedAt: asString(pick(raw, 'updatedAt', 'updated_at')) ?? '', source: asString(pick(raw, 'source')) ?? 'plugins', models }
}

function normalizeMetrics(value: unknown): Metrics {
  const raw = asObject(value); const byPlatform = asObject(pick(raw, 'byPlatform')); const platforms = Array.isArray(raw.platforms) ? raw.platforms.map(asObject) : Object.entries(byPlatform).map(([platform, item]) => ({ platform, ...asObject(item) }))
  const total = asNumber(pick(raw, 'totalRequests', 'requestsToday')) ?? 0; const failures = asNumber(pick(raw, 'failedRequests', 'failuresToday')) ?? 0
  return { totalRequests: total, successfulRequests: asNumber(pick(raw, 'successfulRequests')) ?? Math.max(0, total - failures), failedRequests: failures, averageLatencyMs: asNumber(pick(raw, 'averageLatencyMs', 'avgLatencyMs')) ?? 0, requestsPerMinute: asNumber(pick(raw, 'requestsPerMinute')) ?? 0, requestsToday: asNumber(pick(raw, 'requestsToday')) ?? total, activeRequests: asNumber(pick(raw, 'activeRequests', 'activeConnections')) ?? 0, platforms: platforms.map(item => ({ platform: asString(pick(item, 'platform', 'name')) ?? '', requests: asNumber(pick(item, 'requests', 'requestCount')) ?? 0, errors: asNumber(pick(item, 'errors', 'failures')) ?? 0, averageLatencyMs: asNumber(pick(item, 'averageLatencyMs', 'avgLatencyMs')) ?? 0 })) }
}

function queryString(values: Record<string, unknown>): string {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== '') params.set(key, value instanceof Date ? value.toISOString() : String(value))
  const text = params.toString(); return text ? `?${text}` : ''
}

export async function request<T>(path: string, init: ApiRequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers); const csrf = typeof document !== 'undefined' ? document.cookie.split('; ').find(item => item.startsWith('router_admin_csrf='))?.split('=').slice(1).join('=') : undefined
  if (csrf && ['POST', 'PUT', 'PATCH', 'DELETE'].includes((init.method ?? 'GET').toUpperCase())) headers.set('X-CSRF-Token', decodeURIComponent(csrf))
  let body = init.body
  if (body !== undefined && body !== null && typeof body !== 'string' && !(body instanceof FormData)) { headers.set('Content-Type', 'application/json'); body = JSON.stringify(body) }
  const response = await fetch(path, { ...init, body: body as BodyInit | null | undefined, credentials: 'include', headers })
  const text = await response.text(); let payload: unknown
  if (text) { try { payload = JSON.parse(text) } catch { payload = text } }
  if (!response.ok) { const raw = asObject(payload); throw new ApiError(response.status, asString(raw.error) ?? `请求失败（${response.status}）`) }
  return payload as T
}

function page<T>(value: unknown, map: (value: unknown) => T): PageResult<T> { const raw = asObject(value); const items = asArray(raw.items ?? value).map(map); return { items, page: asNumber(raw.page) ?? 1, pageSize: asNumber(raw.pageSize) ?? 20, total: asNumber(raw.total) ?? items.length, totalPages: asNumber(raw.totalPages) ?? 1 } }

export const api = {
  login: async (username: string, password: string) => normalizePrincipal(await request<unknown>('/api/admin/login', { method: 'POST', body: { username, password } })),
  logout: () => request<void>('/api/admin/logout', { method: 'POST' }),
  me: async () => normalizePrincipal(await request<unknown>('/api/admin/me')),
  metrics: async () => normalizeMetrics(await request<unknown>('/api/admin/metrics')),
  analyticsOverview: (params: { fromUtc?: Date; toUtc?: Date; timezone?: string } = {}) => request<{ range: AnalyticsReport; live: unknown }>(`/api/admin/analytics/overview${queryString(params)}`),
  analytics: (params: { fromUtc?: Date; toUtc?: Date; platform?: string; model?: string; bucket?: string } = {}) => request<AnalyticsReport>(`/api/admin/analytics${queryString(params)}`),
  logs: async (params: PageParams & { fromUtc?: Date; toUtc?: Date; platform?: string; model?: string; keyword?: string } = {}) => page(await request<unknown>(`/api/admin/logs${queryString(params)}`), value => asObject(value) as unknown as RequestLog),
  taskLogs: async (params: PageParams & { fromUtc?: Date; toUtc?: Date; pluginKey?: string; platform?: string; taskName?: string; status?: string; keyword?: string } = {}) => page(await request<unknown>(`/api/admin/task-logs${queryString(params)}`), value => asObject(value) as unknown as TaskLog),
  platforms: async () => (asArray(await request<unknown>('/api/admin/platforms')).map(asObject) as JsonObject[]).map(item => ({ name: asString(pick(item, 'name')) ?? '', pluginKey: asString(pick(item, 'pluginKey')) ?? '', displayName: asString(pick(item, 'displayName')) ?? '', probeEndpoint: asString(pick(item, 'probeEndpoint')), enabled: asBoolean(pick(item, 'enabled')) ?? true })),
  pluginLogs: async (params: PageParams & { fromUtc?: Date; toUtc?: Date; platform?: string; taskName?: string; level?: string; eventType?: string; keyword?: string } = {}) => page(await request<unknown>('/api/admin/plugin-logs' + queryString(params)), value => asObject(value) as unknown as PluginLog),
  accounts: async (params: PageParams & { pluginKey?: string; platform?: string; state?: string; keyword?: string } = {}) => page(await request<unknown>(`/api/admin/accounts${queryString(params)}`), normalizeAccount),
  saveAccount: (platform: string, payload: Record<string, unknown>) => request<Account>(`/api/admin/platforms/${encodeURIComponent(platform)}/accounts`, { method: 'POST', body: payload }),
  deleteAccount: (id: string, pluginKey?: string) => request<void>(`/api/admin/accounts/${encodeURIComponent(id)}${queryString({ pluginKey })}`, { method: 'DELETE' }),
  proxies: async (params: PageParams & { subscriptionIds?: string[]; state?: string } = {}) => page(await request<unknown>(`/api/admin/proxies${queryString({ ...params, subscriptionIds: params.subscriptionIds?.join(',') })}`), normalizeProxy),
  subscriptions: async () => (asArray(await request<unknown>('/api/admin/proxy-subscriptions')).map(normalizeSubscription)),
  saveProxy: (payload: Record<string, unknown>) => request<ProxyEndpoint>('/api/admin/proxies', { method: 'POST', body: payload }),
  deleteProxy: (id: string) => request<void>(`/api/admin/proxies/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  probeProxy: (id: string) => request<ProxyProbeResult>(`/api/admin/proxies/${encodeURIComponent(id)}/probe`, { method: 'POST' }),
  saveSubscription: (payload: Record<string, unknown>) => request<ProxySubscription>('/api/admin/proxy-subscriptions', { method: 'POST', body: payload }),
  deleteSubscription: (id: string) => request<void>(`/api/admin/proxy-subscriptions/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  refreshSubscription: (id: string) => request<{ queued: boolean }>(`/api/admin/proxy-subscriptions/${encodeURIComponent(id)}/refresh`, { method: 'POST' }),
  plugins: async () => (asArray(await request<unknown>('/api/admin/plugins')).map(normalizePlugin)),
  pluginManifest: (pluginKey: string) => request<PluginDescriptor>(`/api/admin/plugins/${encodeURIComponent(pluginKey)}/manifest`),
  pluginPage: (pluginKey: string) => request<string>(`/api/admin/plugins/${encodeURIComponent(pluginKey)}/page`),
  runPluginTask: (pluginKey: string, taskName: string) => request<void>(`/api/admin/plugins/${encodeURIComponent(pluginKey)}/tasks/${encodeURIComponent(taskName)}/run`, { method: 'POST' }),
  reloadPlugins: async () => (asArray(await request<unknown>('/api/admin/plugins/reload', { method: 'POST' })).map(normalizePlugin)),
  setPluginEnabled: (pluginKey: string, enabled: boolean) => request<PluginDescriptor>(`/api/admin/plugins/${encodeURIComponent(pluginKey)}/state`, { method: 'POST', body: { enabled } }),
  deletePlugin: (pluginKey: string) => request<void>(`/api/admin/plugins/${encodeURIComponent(pluginKey)}`, { method: 'DELETE' }),
  pluginRepositories: () => request<PluginRepository[]>('/api/admin/plugin-repositories'),
  addPluginRepository: (owner: string, repo: string) => request<PluginRepository>('/api/admin/plugin-repositories', { method: 'POST', body: { owner, repo } }),
  pluginReleases: (owner: string, repo: string) => request<PluginReleaseSummary[]>(`/api/admin/plugin-repositories/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/releases`),
  pluginReleaseIndex: (owner: string, repo: string, tag: string) => request<PluginReleaseIndex>(`/api/admin/plugin-repositories/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/releases/${encodeURIComponent(tag)}`),
  installPlugins: (owner: string, repo: string, tag: string, pluginIds: string[]) => request<{ installed: string[] }>('/api/admin/plugin-repositories/install', { method: 'POST', body: { owner, repo, tag, pluginIds } }),
  pluginInstallations: () => request<PluginInstallation[]>('/api/admin/plugin-installations'),
  pluginUpdates: () => request<PluginAvailableUpdate[]>('/api/admin/plugin-updates'),
  models: () => request<{ data: ModelDescriptor[] }>('/v1/models'),
  modelPlaza: async () => normalizeModelPlaza(await request<unknown>('/api/admin/model-plaza')),
  refreshModelPlaza: async () => normalizeModelPlaza(await request<unknown>('/api/admin/model-plaza/refresh', { method: 'POST' })),
  refreshModelMetadata: () => request<{ updatedAt: string; modelCount: number; protocolCount: number; protocolsUpdatedAt: string | null }>('/api/admin/model-metadata/refresh', { method: 'POST' }),
  apiKey: () => request<{ key: string }>('/api/admin/api-key'),
  rawApiKey: () => request<{ key: string }>('/api/admin/api-key/raw'),
  rotateApiKey: () => request<{ key: string }>('/api/admin/api-key/rotate', { method: 'POST' }),
  config: () => request<ConfigSnapshot>('/api/admin/config'),
  saveConfig: (payload: Record<string, unknown>) => request<ConfigSnapshot>('/api/admin/config', { method: 'PUT', body: payload }),
  v1Request: (path: string, payload: Record<string, unknown>, key: string) => request<unknown>(path, {
    method: 'POST',
    headers: { Authorization: `Bearer ${key}` },
    body: payload,
  }),
}
