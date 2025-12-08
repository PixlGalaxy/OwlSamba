import { type ElementType, type FormEvent, useEffect, useMemo, useState } from 'react'
import { BrowserRouter, Link, Navigate, Route, Routes, useLocation } from 'react-router-dom'
import {
  BarChart3,
  CircleDot,
  Home,
  ListFilter,
  LockKeyhole,
  LogOut,
  Network,
  Plus,
  RefreshCcw,
  ShieldCheck,
  ShieldOff,
  Settings,
  Undo2,
} from 'lucide-react'
import './App.css'
import './index.css'
import {
  type AuthContextResponse,
  type BanFilters,
  type BanRecord,
  type SettingsResponse,
  type StatsResponse,
  addBan,
  fetchBans,
  fetchStats,
  getAuthContext,
  getSettings,
  login,
  triggerScan,
  unban,
  updateSettings,
} from './api'

function useAuthContext() {
  const [token, setToken] = useState<string | null>(() => localStorage.getItem('authToken'))
  const [context, setContext] = useState<AuthContextResponse | null>(null)
  const [error, setError] = useState<string>('')

  useEffect(() => {
    getAuthContext()
      .then((ctx) => {
        setContext(ctx)
        if (!ctx.requiresAuth) {
          setToken('local')
        }
      })
      .catch((err) => setError(err.message))
  }, [])

  const saveToken = (value: string | null) => {
    if (value) {
      localStorage.setItem('authToken', value)
    } else {
      localStorage.removeItem('authToken')
    }
    setToken(value)
  }

  return { token, context, error, saveToken }
}

function Navigation() {
  const location = useLocation()
  const links = [
    { to: '/', label: 'Home', icon: Home },
    { to: '/bans', label: 'Banned IPs', icon: ShieldOff },
    { to: '/settings', label: 'Settings', icon: Settings },
  ]

  return (
    <nav className="flex items-center justify-between rounded-2xl bg-card/70 p-4 shadow-lg shadow-black/20 ring-1 ring-white/5">
      <div className="flex items-center gap-3">
        <ShieldCheck className="text-cyan-300" />
        <div>
          <p className="text-sm text-slate-400">OwlSamba</p>
          <p className="font-semibold text-slate-100">SMB Guardian</p>
        </div>
      </div>
      <div className="flex gap-2 text-sm font-medium">
        {links.map((link) => {
          const active = location.pathname === link.to
          const Icon = link.icon
          return (
            <Link
              key={link.to}
              to={link.to}
              className={`flex items-center gap-2 rounded-xl px-4 py-2 transition-all ${
                active
                  ? 'bg-cyan-500/20 text-cyan-100 ring-1 ring-cyan-400/70'
                  : 'text-slate-300 hover:bg-white/5'
              }`}
            >
              <Icon size={18} />
              {link.label}
            </Link>
          )
        })}
      </div>
    </nav>
  )
}

function LoginView({ onAuthenticated }: { onAuthenticated: (token: string) => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setError('')
    try {
      const token = await login(username, password)
      onAuthenticated(token)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="app-shell flex min-h-screen items-center justify-center bg-surface px-4">
      <div className="w-full max-w-md rounded-3xl bg-card/70 p-8 shadow-2xl shadow-black/30 ring-1 ring-white/5">
        <div className="mb-6 flex items-center gap-3">
          <LockKeyhole className="text-cyan-300" />
          <div>
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Secure Access</p>
            <p className="text-lg font-semibold text-white">Sign in to continue</p>
          </div>
        </div>
        {error && <p className="mb-4 rounded-xl bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
        <form className="space-y-4" onSubmit={submit}>
          <div>
            <label className="text-sm text-slate-300">Usuario</label>
            <input
              className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
              autoComplete="username"
            />
          </div>
          <div>
            <label className="text-sm text-slate-300">Contraseña</label>
            <input
              type="password"
              className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              autoComplete="current-password"
            />
          </div>
          <button
            disabled={loading}
            className="flex w-full items-center justify-center gap-2 rounded-xl bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-3 font-semibold text-white shadow-lg shadow-cyan-500/20 hover:from-cyan-400 hover:to-blue-400 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <ShieldCheck size={18} /> {loading ? 'Verificando...' : 'Entrar'}
          </button>
        </form>
        <p className="mt-4 text-center text-xs text-slate-500">
          El panel solo requiere inicio de sesión cuando no estás en localhost.
        </p>
      </div>
    </div>
  )
}

function StatCard({ icon: Icon, title, value, helper }: { icon: ElementType; title: string; value: string; helper?: string }) {
  return (
    <div className="rounded-2xl bg-card/70 p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-sm uppercase tracking-wide text-slate-400">{title}</p>
          <p className="text-3xl font-semibold text-white">{value}</p>
          {helper && <p className="text-xs text-slate-500">{helper}</p>}
        </div>
        <div className="rounded-xl bg-cyan-500/10 p-3 text-cyan-300">
          <Icon />
        </div>
      </div>
    </div>
  )
}

function ActivityChart({ data }: { data: StatsResponse }) {
  const max = useMemo(() => Math.max(...data.timeline.map((t) => t.attempts), 1), [data])

  return (
    <div className="rounded-2xl bg-card/70 p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="mb-4 flex items-center gap-3 text-sm text-slate-300">
        <BarChart3 className="text-cyan-300" />
        <div>
          <p className="text-xs uppercase tracking-[0.18em] text-slate-400">Actividad</p>
          <p className="font-semibold text-white">Intentos bloqueados (últimos {data.window} días)</p>
        </div>
      </div>
      <div className="flex h-48 items-end gap-3">
        {data.timeline.length === 0 && <p className="text-sm text-slate-500">No hay intentos registrados.</p>}
        {data.timeline.map((entry) => {
          const height = `${(entry.attempts / max) * 100}%`
          return (
            <div key={entry.date} className="flex flex-1 flex-col items-center gap-2">
              <div className="flex h-full w-full items-end rounded-xl bg-slate-900/60 p-1">
                <div className="w-full rounded-lg bg-gradient-to-t from-cyan-500/80 to-cyan-300/70" style={{ height }} />
              </div>
              <div className="text-xs text-slate-400">
                <p className="font-semibold text-slate-200">{entry.attempts}</p>
                <p>{entry.date}</p>
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function Dashboard({ token, host }: { token: string | null; host: string }) {
  const [stats, setStats] = useState<StatsResponse | null>(null)
  const [days, setDays] = useState(7)
  const [error, setError] = useState('')

  useEffect(() => {
    fetchStats(token, days)
      .then(setStats)
      .catch((err) => setError(err.message))
  }, [token, days])

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between gap-4">
        <div>
          <p className="text-sm text-slate-400">Host</p>
          <p className="text-2xl font-semibold text-white">{host}</p>
        </div>
        <div className="flex items-center gap-3">
          <label className="text-sm text-slate-400">Rango</label>
          <select
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
            className="rounded-xl bg-slate-900/70 px-3 py-2 text-slate-100 ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          >
            {[7, 14, 30].map((option) => (
              <option key={option} value={option}>
                {option} días
              </option>
            ))}
          </select>
        </div>
      </div>
      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-sm text-rose-300">{error}</p>}
      {stats && (
        <>
          <div className="grid gap-4 md:grid-cols-3">
            <StatCard icon={Network} title="IPs baneadas" value={stats.totalBanned.toString()} helper="Totales" />
            <StatCard
              icon={CircleDot}
              title="Últimos 7 días"
              value={stats.recentBanned.toString()}
              helper="Baneos recientes"
            />
            <StatCard icon={ShieldCheck} title="Umbral" value={`${stats.window} días activos`} helper="Ventana seleccionada" />
          </div>
          <ActivityChart data={stats} />
        </>
      )}
    </div>
  )
}

function BanFiltersBar({ filters, onChange }: { filters: BanFilters; onChange: (next: BanFilters) => void }) {
  return (
    <div className="flex flex-wrap items-end gap-3 rounded-2xl bg-card/70 p-4 ring-1 ring-white/5">
      <div className="flex items-center gap-2 text-slate-300">
        <ListFilter size={18} className="text-cyan-300" />
        <p className="text-sm font-semibold">Filtros</p>
      </div>
      <label className="text-sm text-slate-300">
        Intentos mínimos
        <input
          type="number"
          className="mt-1 w-28 rounded-lg bg-slate-900/60 px-2 py-1 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          value={filters.min_attempts ?? 0}
          min={0}
          onChange={(e) => onChange({ ...filters, min_attempts: Number(e.target.value) })}
        />
      </label>
      <label className="text-sm text-slate-300">
        Desde
        <input
          type="date"
          className="mt-1 rounded-lg bg-slate-900/60 px-2 py-1 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          value={filters.start_date ?? ''}
          onChange={(e) => onChange({ ...filters, start_date: e.target.value || undefined })}
        />
      </label>
      <label className="text-sm text-slate-300">
        Hasta
        <input
          type="date"
          className="mt-1 rounded-lg bg-slate-900/60 px-2 py-1 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          value={filters.end_date ?? ''}
          onChange={(e) => onChange({ ...filters, end_date: e.target.value || undefined })}
        />
      </label>
      <label className="text-sm text-slate-300">
        Orden
        <select
          className="mt-1 rounded-lg bg-slate-900/60 px-2 py-1 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          value={filters.sort_by ?? 'last_attempt'}
          onChange={(e) => onChange({ ...filters, sort_by: e.target.value })}
        >
          <option value="last_attempt">Fecha</option>
          <option value="attempts">Intentos</option>
          <option value="ip">IP</option>
        </select>
      </label>
      <label className="text-sm text-slate-300">
        Dirección
        <select
          className="mt-1 rounded-lg bg-slate-900/60 px-2 py-1 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          value={filters.sort_order ?? 'desc'}
          onChange={(e) => onChange({ ...filters, sort_order: e.target.value })}
        >
          <option value="desc">Desc</option>
          <option value="asc">Asc</option>
        </select>
      </label>
    </div>
  )
}

function BansPage({ token }: { token: string | null }) {
  const [filters, setFilters] = useState<BanFilters>({ min_attempts: 0, sort_by: 'last_attempt', sort_order: 'desc' })
  const [entries, setEntries] = useState<BanRecord[]>([])
  const [error, setError] = useState('')
  const [form, setForm] = useState({ ip: '', attempts: 10, workstation: '', user: '' })
  const [loading, setLoading] = useState(false)

  const load = () => {
    setLoading(true)
    fetchBans(token, filters)
      .then(setEntries)
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false))
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filters, token])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError('')
    try {
      await addBan(token, form)
      setForm({ ip: '', attempts: 10, workstation: '', user: '' })
      load()
    } catch (err) {
      setError((err as Error).message)
    }
  }

  const handleUnban = async (ip: string) => {
    try {
      await unban(token, ip)
      load()
    } catch (err) {
      setError((err as Error).message)
    }
  }

  return (
    <div className="space-y-4">
      <BanFiltersBar filters={filters} onChange={setFilters} />
      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-sm text-rose-300">{error}</p>}
      <div className="overflow-hidden rounded-2xl bg-card/70 ring-1 ring-white/5">
        <div className="grid grid-cols-6 gap-2 bg-white/5 px-4 py-2 text-xs uppercase tracking-[0.18em] text-slate-400">
          <span>IP</span>
          <span>Intentos</span>
          <span>Último intento</span>
          <span>Workstation</span>
          <span>Usuario</span>
          <span className="text-right">Acciones</span>
        </div>
        <div className="divide-y divide-white/5">
          {entries.map((item) => (
            <div key={item.ip} className="grid grid-cols-6 gap-2 px-4 py-3 text-sm text-slate-100">
              <span className="font-mono">{item.ip}</span>
              <span className="font-semibold text-cyan-200">{item.attempts}</span>
              <span>{item.last_attempt ? new Date(item.last_attempt).toLocaleString() : 'N/A'}</span>
              <span>{item.workstation ?? '-'}</span>
              <span>{item.last_user ?? '-'}</span>
              <div className="flex justify-end gap-2">
                {item.banned ? (
                  <span className="rounded-lg bg-emerald-500/20 px-2 py-1 text-xs text-emerald-200 ring-1 ring-emerald-400/40">Baneada</span>
                ) : (
                  <span className="rounded-lg bg-amber-500/20 px-2 py-1 text-xs text-amber-200 ring-1 ring-amber-400/40">Monitoreada</span>
                )}
                <button
                  className="rounded-lg bg-white/5 px-2 py-1 text-xs text-slate-200 ring-1 ring-white/10 hover:bg-rose-500/20"
                  onClick={() => handleUnban(item.ip)}
                >
                  <Undo2 size={14} />
                </button>
              </div>
            </div>
          ))}
          {!entries.length && (
            <p className="px-4 py-6 text-center text-sm text-slate-500">No hay IPs registradas con los filtros actuales.</p>
          )}
        </div>
      </div>
      <div className="rounded-2xl bg-card/70 p-5 ring-1 ring-white/5">
        <h3 className="mb-3 flex items-center gap-2 text-sm font-semibold text-white">
          <Plus size={18} className="text-cyan-300" /> Añadir manualmente
        </h3>
        <form className="grid gap-3 md:grid-cols-4" onSubmit={submit}>
          <input
            className="rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            placeholder="IP"
            value={form.ip}
            required
            onChange={(e) => setForm({ ...form, ip: e.target.value })}
          />
          <input
            type="number"
            min={1}
            className="rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            placeholder="Intentos"
            value={form.attempts}
            onChange={(e) => setForm({ ...form, attempts: Number(e.target.value) })}
          />
          <input
            className="rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            placeholder="Workstation"
            value={form.workstation}
            onChange={(e) => setForm({ ...form, workstation: e.target.value })}
          />
          <input
            className="rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            placeholder="Usuario"
            value={form.user}
            onChange={(e) => setForm({ ...form, user: e.target.value })}
          />
          <button
            className="md:col-span-4 mt-2 flex items-center justify-center gap-2 rounded-xl bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-3 font-semibold text-white shadow-lg shadow-cyan-500/20"
          >
            <ShieldCheck size={18} /> Guardar IP
          </button>
        </form>
      </div>
      {loading && <p className="text-sm text-slate-400">Cargando datos...</p>}
    </div>
  )}

function SettingsPage({ token }: { token: string | null }) {
  const [settings, setSettings] = useState<SettingsResponse | null>(null)
  const [error, setError] = useState('')
  const [status, setStatus] = useState('')

  useEffect(() => {
    getSettings(token)
      .then(setSettings)
      .catch((err) => setError(err.message))
  }, [token])

  const handleChange = (key: keyof SettingsResponse, value: string | number | boolean | string[]) => {
    if (!settings) return
    setSettings({ ...settings, [key]: value } as SettingsResponse)
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    if (!settings) return
    setError('')
    try {
      const updated = await updateSettings(token, settings)
      setSettings(updated)
      setStatus('Guardado')
      setTimeout(() => setStatus(''), 2000)
    } catch (err) {
      setError((err as Error).message)
    }
  }

  const runScan = async () => {
    try {
      setStatus('Escaneando...')
      const count = await triggerScan(token)
      setStatus(`Procesados ${count} eventos`)
    } catch (err) {
      setError((err as Error).message)
    }
  }

  if (!settings) return <p className="text-slate-400">Cargando configuración...</p>

  return (
    <form className="space-y-4" onSubmit={save}>
      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-sm text-rose-300">{error}</p>}
      <div className="grid gap-4 md:grid-cols-2">
        <label className="text-sm text-slate-300">
          Umbral de intentos
          <input
            type="number"
            min={1}
            className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            value={settings.threshold}
            onChange={(e) => handleChange('threshold', Number(e.target.value))}
          />
        </label>
        <label className="text-sm text-slate-300">
          Espera entre escaneos (min)
          <input
            type="number"
            min={1}
            className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            value={settings.scan_wait}
            onChange={(e) => handleChange('scan_wait', Number(e.target.value))}
          />
        </label>
        <label className="text-sm text-slate-300">
          Log a monitorear
          <input
            className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            value={settings.log_name}
            onChange={(e) => handleChange('log_name', e.target.value)}
          />
        </label>
        <label className="text-sm text-slate-300">
          Event ID
          <input
            type="number"
            min={1}
            className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            value={settings.event_id}
            onChange={(e) => handleChange('event_id', Number(e.target.value))}
          />
        </label>
        <label className="text-sm text-slate-300">
          Whitelist IPs (separadas por coma)
          <input
            className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            value={settings.whitelist_ips.join(', ')}
            onChange={(e) => handleChange('whitelist_ips', e.target.value.split(',').map((item) => item.trim()).filter(Boolean))}
          />
        </label>
        <label className="text-sm text-slate-300">
          Whitelist dominios (coma)
          <input
            className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            value={settings.whitelist_domains.join(', ')}
            onChange={(e) => handleChange('whitelist_domains', e.target.value.split(',').map((item) => item.trim()).filter(Boolean))}
          />
        </label>
        <label className="flex items-center gap-2 text-sm text-slate-300">
          <input
            type="checkbox"
            className="h-4 w-4 rounded border border-slate-500 bg-slate-900"
            checked={settings.ban_ips}
            onChange={(e) => handleChange('ban_ips', e.target.checked)}
          />
          Añadir regla al firewall
        </label>
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <button className="flex items-center gap-2 rounded-xl bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-3 font-semibold text-white shadow-lg shadow-cyan-500/20">
          <Settings size={18} /> Guardar cambios
        </button>
        <button
          type="button"
          onClick={runScan}
          className="flex items-center gap-2 rounded-xl bg-white/5 px-4 py-3 text-slate-200 ring-1 ring-white/10 hover:bg-white/10"
        >
          <RefreshCcw size={18} /> Forzar escaneo
        </button>
        {status && <span className="text-sm text-emerald-200">{status}</span>}
      </div>
    </form>
  )
}

function AppShell() {
  const { token, context, error, saveToken } = useAuthContext()
  const authenticated = Boolean(token)

  if (error) {
    return <p className="p-4 text-rose-300">{error}</p>
  }

  if (!context) {
    return <p className="p-4 text-slate-400">Verificando acceso...</p>
  }

  if (context.requiresAuth && !authenticated) {
    return <LoginView onAuthenticated={saveToken} />
  }

  const logout = () => saveToken(null)

  return (
    <div className="app-shell min-h-screen bg-surface px-4 py-6 text-slate-50">
      <div className="mx-auto flex max-w-6xl flex-col gap-4">
        <Navigation />
        <div className="flex items-center justify-between rounded-2xl bg-card/70 px-4 py-3 ring-1 ring-white/5">
          <div className="flex items-center gap-2 text-sm text-slate-300">
            <ShieldCheck className="text-cyan-300" size={18} />
            Acceso {context.requiresAuth ? 'seguro' : 'local sin autenticación'}
          </div>
          <button
            onClick={logout}
            className="flex items-center gap-2 rounded-xl bg-white/5 px-3 py-2 text-xs text-slate-200 ring-1 ring-white/10 hover:bg-rose-500/20"
          >
            <LogOut size={14} /> Salir
          </button>
        </div>
        <Routes>
          <Route path="/" element={<Dashboard token={token} host={context.host} />} />
          <Route path="/bans" element={<BansPage token={token} />} />
          <Route path="/settings" element={<SettingsPage token={token} />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </div>
    </div>
  )
}

function App() {
  return (
    <BrowserRouter>
      <AppShell />
    </BrowserRouter>
  )
}

export default App
