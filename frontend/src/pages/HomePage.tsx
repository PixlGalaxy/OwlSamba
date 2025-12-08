import { useEffect, useState } from 'react'
import { CircleDot, Network, ShieldCheck } from 'lucide-react'
import { fetchStats, StatsResponse } from '../api'
import { StatCard } from '../components/StatCard'
import { ActivityChart } from '../components/ActivityChart'

export function HomePage({ token, host }: { token: string | null; host: string }) {
  const [stats, setStats] = useState<StatsResponse | null>(null)
  const [error, setError] = useState('')
  const [days, setDays] = useState(7)

  useEffect(() => {
    fetchStats(token, days)
      .then(setStats)
      .catch((err) => setError(err.message))
  }, [token, days])

  return (
    <div className="space-y-6">
      <div className="rounded-2xl bg-gradient-to-r from-cyan-500/20 via-blue-500/10 to-purple-500/10 p-6 ring-1 ring-white/10">
        <p className="text-sm uppercase tracking-[0.18em] text-cyan-200">Host</p>
        <p className="text-3xl font-semibold text-white">{host || 'Desconocido'}</p>
        <p className="text-sm text-slate-200">Monitoreo de SMB con bloqueo automático</p>
      </div>

      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-rose-200">{error}</p>}

      {stats ? (
        <>
          <div className="grid gap-4 md:grid-cols-3">
            <StatCard icon={ShieldCheck} title="IPs baneadas" value={stats.totalBanned.toString()} />
            <StatCard
              icon={CircleDot}
              title={`Baneos últimos ${days} días`}
              value={stats.recentBanned.toString()}
              helper="Ajusta la ventana para más contexto"
            />
            <StatCard icon={Network} title="Ventana" value={`${days} días`} helper="Puedes ver hasta 30 días" />
          </div>
          <div className="flex items-center gap-3 text-sm text-slate-300">
            <span>Ventana de tiempo:</span>
            <select
              value={days}
              onChange={(e) => setDays(parseInt(e.target.value))}
              className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            >
              {[7, 14, 21, 30].map((d) => (
                <option key={d} value={d}>
                  {d} días
                </option>
              ))}
            </select>
          </div>
          <ActivityChart data={stats} />
        </>
      ) : (
        <p className="text-slate-300">Cargando estadísticas...</p>
      )}
    </div>
  )
}

