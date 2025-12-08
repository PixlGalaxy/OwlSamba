export interface AuthContextResponse {
  requiresAuth: boolean
  host: string
}

export interface StatsResponse {
  totalBanned: number
  recentBanned: number
  timeline: { date: string; attempts: number }[]
  host: string
  window: number
}

export interface BanRecord {
  ip: string
  attempts: number
  last_attempt?: string
  workstation?: string
  last_user?: string
  banned?: number
  banned_time?: string | null
  manual?: number
}

export interface SettingsResponse {
  threshold: number
  scan_wait: number
  ban_ips: boolean
  log_name: string
  event_id: number
  whitelist_ips: string[]
  whitelist_domains: string[]
  database_file?: string
  hostname?: string
}

export interface ScanStatus {
  running: boolean
  mode: string | null
  lastStarted: string | null
  lastFinished: string | null
  nextScheduled: string | null
  lastProcessed: number | null
}

const API_BASE = import.meta.env.VITE_API_BASE ?? 'http://localhost:8000'

function authHeaders(token?: string): HeadersInit {
  const headers: HeadersInit = {
    'Content-Type': 'application/json',
  }
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }
  return headers
}

async function handle<T>(response: Response): Promise<T> {
  if (response.status === 204) return {} as T

  const parseJson = async () => {
    try {
      return (await response.json()) as T
    } catch (err) {
      const text = await response.text()
      const message = text?.startsWith('<') ? 'API response is not valid JSON' : text
      throw new Error(message || (err as Error).message)
    }
  }

  if (!response.ok) {
    let message = 'Request failed'
    try {
      const data = await response.clone().json()
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      message = (data as any).detail ?? message
    } catch (e) {
      message = await response.text()
    }
    throw new Error(message || response.statusText)
  }

  return parseJson()
}

export async function getAuthContext(): Promise<AuthContextResponse> {
  const res = await fetch(`${API_BASE}/api/auth/context`)
  return handle<AuthContextResponse>(res)
}

export async function login(username: string, password: string): Promise<string> {
  const res = await fetch(`${API_BASE}/api/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  const data = await handle<{ token: string }>(res)
  return data.token
}

export async function logout(token: string | null): Promise<void> {
  const res = await fetch(`${API_BASE}/api/logout`, {
    method: 'POST',
    headers: authHeaders(token || undefined),
  })
  await handle(res)
}

export async function fetchStats(token: string | null, days: number): Promise<StatsResponse> {
  const res = await fetch(`${API_BASE}/api/stats?days=${days}`, {
    headers: authHeaders(token || undefined),
  })
  return handle<StatsResponse>(res)
}

export interface BanFilters {
  min_attempts?: number
  start_date?: string
  end_date?: string
  sort_by?: string
  sort_order?: string
}

export async function fetchBans(token: string | null, filters: BanFilters): Promise<BanRecord[]> {
  const search = new URLSearchParams()
  if (filters.min_attempts) search.set('min_attempts', filters.min_attempts.toString())
  if (filters.start_date) search.set('start_date', filters.start_date)
  if (filters.end_date) search.set('end_date', filters.end_date)
  if (filters.sort_by) search.set('sort_by', filters.sort_by)
  if (filters.sort_order) search.set('sort_order', filters.sort_order)
  const res = await fetch(`${API_BASE}/api/bans?${search.toString()}`, {
    headers: authHeaders(token || undefined),
  })
  return handle<BanRecord[]>(res)
}

export async function addBan(
  token: string | null,
  payload: { ip: string; attempts?: number; workstation?: string; user?: string },
): Promise<void> {
  const res = await fetch(`${API_BASE}/api/bans`, {
    method: 'POST',
    headers: authHeaders(token || undefined),
    body: JSON.stringify(payload),
  })
  await handle(res)
}

export async function unban(token: string | null, ip: string): Promise<void> {
  const res = await fetch(`${API_BASE}/api/bans/${ip}`, {
    method: 'DELETE',
    headers: authHeaders(token || undefined),
  })
  await handle(res)
}

export async function getSettings(token: string | null): Promise<SettingsResponse> {
  const res = await fetch(`${API_BASE}/api/settings`, {
    headers: authHeaders(token || undefined),
  })
  return handle<SettingsResponse>(res)
}

export async function updateSettings(token: string | null, payload: Partial<SettingsResponse>): Promise<SettingsResponse> {
  const res = await fetch(`${API_BASE}/api/settings`, {
    method: 'PUT',
    headers: authHeaders(token || undefined),
    body: JSON.stringify(payload),
  })
  return handle<SettingsResponse>(res)
}

export async function triggerScan(token: string | null): Promise<number> {
  const res = await fetch(`${API_BASE}/api/scan`, {
    method: 'POST',
    headers: authHeaders(token || undefined),
  })
  const data = await handle<{ status: string }>(res)
  return data.status === 'started' ? 1 : 0
}

export async function getScanStatus(token: string | null): Promise<ScanStatus> {
  const res = await fetch(`${API_BASE}/api/scan/status`, {
    headers: authHeaders(token || undefined),
  })
  return handle<ScanStatus>(res)
}
