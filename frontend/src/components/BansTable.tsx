import { useMemo, useState, FormEvent } from 'react'
import { ListFilter, Plus, RefreshCcw, Undo2 } from 'lucide-react'
import type { BanFilters, BanRecord } from '../api'

export function BansTable({
  bans,
  filters,
  setFilters,
  onRefresh,
  onUnban,
  onAdd,
  loading,
}: {
  bans: BanRecord[]
  filters: BanFilters
  setFilters: (f: BanFilters) => void
  onRefresh: () => void
  onUnban: (ip: string) => Promise<void>
  onAdd: (payload: { ip: string; attempts?: number; workstation?: string; user?: string }) => Promise<void>
  loading: boolean
}) {
  const sorted = useMemo(() => bans, [bans])

  return (
    <div className="rounded-2xl bg-card/70 p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div className="flex items-center gap-2 text-slate-200">
          <ListFilter className="text-cyan-300" />
          <span className="font-semibold">IPs baneadas</span>
          <button
            onClick={onRefresh}
            className="flex items-center gap-1 rounded-lg bg-slate-800/60 px-3 py-1 text-sm text-slate-200 transition hover:bg-slate-700"
          >
            <RefreshCcw size={16} />
            Refrescar
          </button>
        </div>
        <div className="flex flex-wrap gap-2 text-sm">
          <input
            type="number"
            placeholder="Min intentos"
            value={filters.min_attempts ?? ''}
            onChange={(e) => setFilters({ ...filters, min_attempts: e.target.value ? parseInt(e.target.value) : undefined })}
            className="w-32 rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
          <input
            type="date"
            value={filters.start_date ?? ''}
            onChange={(e) => setFilters({ ...filters, start_date: e.target.value || undefined })}
            className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
          <input
            type="date"
            value={filters.end_date ?? ''}
            onChange={(e) => setFilters({ ...filters, end_date: e.target.value || undefined })}
            className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
          <select
            value={filters.sort_by ?? ''}
            onChange={(e) => setFilters({ ...filters, sort_by: e.target.value || undefined })}
            className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          >
            <option value="">Orden</option>
            <option value="attempts">Intentos</option>
            <option value="last_attempt">Fecha</option>
          </select>
          <select
            value={filters.sort_order ?? ''}
            onChange={(e) => setFilters({ ...filters, sort_order: e.target.value || undefined })}
            className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          >
            <option value="">Asc/Desc</option>
            <option value="asc">Asc</option>
            <option value="desc">Desc</option>
          </select>
        </div>
      </div>

      <AddBanForm onAdd={onAdd} />

      <div className="mt-4 overflow-x-auto">
        <table className="min-w-full text-sm text-slate-200">
          <thead>
            <tr className="text-left text-slate-400">
              <th className="px-3 py-2">IP</th>
              <th className="px-3 py-2">Intentos</th>
              <th className="px-3 py-2">Último intento</th>
              <th className="px-3 py-2">Workstation</th>
              <th className="px-3 py-2">Usuario</th>
              <th className="px-3 py-2">Estado</th>
              <th className="px-3 py-2"></th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((ban) => (
              <tr key={ban.ip} className="border-b border-white/5">
                <td className="px-3 py-2 font-mono text-cyan-100">{ban.ip}</td>
                <td className="px-3 py-2">{ban.attempts}</td>
                <td className="px-3 py-2">{ban.last_attempt ?? '—'}</td>
                <td className="px-3 py-2">{ban.workstation ?? '—'}</td>
                <td className="px-3 py-2">{ban.last_user ?? '—'}</td>
                <td className="px-3 py-2">
                  {ban.banned_time ? 'Baneada' : 'Permitida'}
                  {ban.manual ? ' (Manual)' : ''}
                </td>
                <td className="px-3 py-2 text-right">
                  {ban.banned_time ? (
                    <button
                      disabled={loading}
                      onClick={() => onUnban(ban.ip)}
                      className="flex items-center gap-1 rounded-lg bg-rose-500/10 px-3 py-1 text-rose-200 ring-1 ring-rose-500/40 transition hover:bg-rose-500/20 disabled:opacity-60"
                    >
                      <Undo2 size={16} />
                      Desbanear
                    </button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function AddBanForm({ onAdd }: { onAdd: (payload: { ip: string; attempts?: number; workstation?: string; user?: string }) => Promise<void> }) {
  const [ip, setIp] = useState('')
  const [attempts, setAttempts] = useState('')
  const [workstation, setWorkstation] = useState('')
  const [user, setUser] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setError('')
    try {
      await onAdd({
        ip,
        attempts: attempts ? parseInt(attempts) : undefined,
        workstation: workstation || undefined,
        user: user || undefined,
      })
      setIp('')
      setAttempts('')
      setWorkstation('')
      setUser('')
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
  }

  return (
    <form onSubmit={submit} className="space-y-2 rounded-xl bg-slate-900/40 p-3">
      <div className="flex items-center gap-2 text-sm text-slate-300">
        <Plus size={16} className="text-cyan-300" />
        Agregar IP manualmente
      </div>
      {error && <p className="rounded-lg bg-rose-500/10 px-3 py-2 text-xs text-rose-200">{error}</p>}
      <div className="grid gap-2 md:grid-cols-4">
        <input
          required
          placeholder="IP"
          value={ip}
          onChange={(e) => setIp(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
        <input
          type="number"
          placeholder="Intentos"
          value={attempts}
          onChange={(e) => setAttempts(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
        <input
          placeholder="Workstation"
          value={workstation}
          onChange={(e) => setWorkstation(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
        <input
          placeholder="Usuario"
          value={user}
          onChange={(e) => setUser(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
      </div>
      <button
        disabled={loading}
        className="flex items-center gap-2 rounded-lg bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-2 text-sm font-semibold text-white shadow-cyan-500/20 transition hover:from-cyan-400 hover:to-blue-400 disabled:opacity-60"
      >
        <Plus size={16} /> Añadir
      </button>
    </form>
  )
}

