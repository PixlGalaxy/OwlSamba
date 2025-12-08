import { useMemo, useState } from 'react'
import type { FormEvent } from 'react'
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
    <div className="rounded-2xl bg-card/70 p-3 sm:p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="mb-4 flex flex-col gap-3">
        <div className="flex items-center gap-2 text-slate-200">
          <ListFilter className="text-cyan-300 flex-shrink-0" />
          <span className="font-semibold text-sm sm:text-base">Banned IPs</span>
          <button
            onClick={onRefresh}
            className="flex items-center gap-1 rounded-lg bg-slate-800/60 px-2 sm:px-3 py-1 text-xs sm:text-sm text-slate-200 transition hover:bg-slate-700"
          >
            <RefreshCcw size={16} />
            <span className="hidden sm:inline">Refresh</span>
          </button>
        </div>
        <div className="flex flex-col gap-2 text-xs sm:text-sm">
          <div className="flex flex-wrap gap-2">
            <input
              type="number"
              placeholder="Min attempts"
              value={filters.min_attempts ?? ''}
              onChange={(e) => setFilters({ ...filters, min_attempts: e.target.value ? parseInt(e.target.value) : undefined })}
              className="flex-1 min-w-[120px] rounded-lg bg-slate-900/60 px-2 sm:px-3 py-1.5 sm:py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            />
            <input
              type="date"
              value={filters.start_date ?? ''}
              onChange={(e) => setFilters({ ...filters, start_date: e.target.value || undefined })}
              className="flex-1 min-w-[120px] rounded-lg bg-slate-900/60 px-2 sm:px-3 py-1.5 sm:py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            />
            <input
              type="date"
              value={filters.end_date ?? ''}
              onChange={(e) => setFilters({ ...filters, end_date: e.target.value || undefined })}
              className="flex-1 min-w-[120px] rounded-lg bg-slate-900/60 px-2 sm:px-3 py-1.5 sm:py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            />
          </div>
          <div className="flex flex-wrap gap-2">
            <select
              value={filters.sort_by ?? ''}
              onChange={(e) => setFilters({ ...filters, sort_by: e.target.value || undefined })}
              className="flex-1 min-w-[100px] rounded-lg bg-slate-900/60 px-2 sm:px-3 py-1.5 sm:py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            >
              <option value="">Order</option>
              <option value="attempts">Attempts</option>
              <option value="last_attempt">Date</option>
            </select>
            <select
              value={filters.sort_order ?? ''}
              onChange={(e) => setFilters({ ...filters, sort_order: e.target.value || undefined })}
              className="flex-1 min-w-[100px] rounded-lg bg-slate-900/60 px-2 sm:px-3 py-1.5 sm:py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            >
              <option value="">Asc/Desc</option>
              <option value="asc">Asc</option>
              <option value="desc">Desc</option>
            </select>
          </div>
        </div>
      </div>

      <AddBanForm onAdd={onAdd} />

      <div className="mt-4 overflow-x-auto">
        <table className="w-full text-xs sm:text-sm text-slate-200">
          <thead>
            <tr className="text-left text-slate-400 border-b border-white/5">
              <th className="px-2 sm:px-3 py-2 whitespace-nowrap">IP</th>
              <th className="px-2 sm:px-3 py-2 whitespace-nowrap">Attempts</th>
              <th className="hidden sm:table-cell px-2 sm:px-3 py-2 whitespace-nowrap">Last attempt</th>
              <th className="hidden md:table-cell px-2 sm:px-3 py-2 whitespace-nowrap">Workstation</th>
              <th className="hidden lg:table-cell px-2 sm:px-3 py-2 whitespace-nowrap">User</th>
              <th className="px-2 sm:px-3 py-2 whitespace-nowrap">Status</th>
              <th className="px-2 sm:px-3 py-2 whitespace-nowrap"></th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((ban) => (
              <tr key={ban.ip} className="border-b border-white/5 hover:bg-white/2">
                <td className="px-2 sm:px-3 py-2 font-mono text-cyan-100 whitespace-nowrap text-xs sm:text-sm">{ban.ip}</td>
                <td className="px-2 sm:px-3 py-2 whitespace-nowrap">{ban.attempts}</td>
                <td className="hidden sm:table-cell px-2 sm:px-3 py-2 whitespace-nowrap text-xs">{ban.last_attempt ?? '—'}</td>
                <td className="hidden md:table-cell px-2 sm:px-3 py-2 whitespace-nowrap truncate text-xs">{ban.workstation ?? '—'}</td>
                <td className="hidden lg:table-cell px-2 sm:px-3 py-2 whitespace-nowrap truncate text-xs">{ban.last_user ?? '—'}</td>
                <td className="px-2 sm:px-3 py-2 whitespace-nowrap text-xs">
                  {ban.banned_time ? 'Blocked' : 'Allowed'}
                  {ban.manual ? ' (M)' : ''}
                </td>
                <td className="px-2 sm:px-3 py-2 text-right whitespace-nowrap">
                  {ban.banned_time ? (
                    <button
                      disabled={loading}
                      onClick={() => onUnban(ban.ip)}
                      className="flex items-center gap-1 rounded-lg bg-rose-500/10 px-2 sm:px-3 py-1 text-xs text-rose-200 ring-1 ring-rose-500/40 transition hover:bg-rose-500/20 disabled:opacity-60"
                    >
                      <Undo2 size={14} className="hidden sm:inline" />
                      <span className="hidden sm:inline">Unban</span>
                      <span className="sm:hidden">✕</span>
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
      <div className="flex items-center gap-2 text-xs sm:text-sm text-slate-300">
        <Plus size={16} className="text-cyan-300 flex-shrink-0" />
        <span className="font-medium">Add IP manually</span>
      </div>
      {error && <p className="rounded-lg bg-rose-500/10 px-3 py-2 text-xs text-rose-200">{error}</p>}
      <div className="grid gap-2 grid-cols-1 sm:grid-cols-4">
        <input
          required
          placeholder="IP"
          value={ip}
          onChange={(e) => setIp(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-sm text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
        <input
          type="number"
          placeholder="Attempts"
          value={attempts}
          onChange={(e) => setAttempts(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-sm text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
        <input
          placeholder="Workstation"
          value={workstation}
          onChange={(e) => setWorkstation(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-sm text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
        <input
          placeholder="User"
          value={user}
          onChange={(e) => setUser(e.target.value)}
          className="rounded-lg bg-slate-900/60 px-3 py-2 text-sm text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
        />
      </div>
      <button
        disabled={loading}
        className="w-full flex items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-2 text-xs sm:text-sm font-semibold text-white shadow-cyan-500/20 transition hover:from-cyan-400 hover:to-blue-400 disabled:opacity-60"
      >
        <Plus size={16} /> Add
      </button>
    </form>
  )
}

