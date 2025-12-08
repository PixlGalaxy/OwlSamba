import { useEffect, useState } from 'react'
import { AlarmClock, CircleDot, Network, ShieldCheck } from 'lucide-react'
import { fetchStats, getScanStatus } from '../api'
import type { ScanStatus, StatsResponse } from '../api'
import { StatCard } from '../components/StatCard'
import { ActivityChart } from '../components/ActivityChart'
import { ScanStatusCard } from '../components/ScanStatusCard'

export function HomePage({ token, host }: { token: string | null; host: string }) {
  const [stats, setStats] = useState<StatsResponse | null>(null)
  const [error, setError] = useState('')
  const [days, setDays] = useState(7)
  const [scanStatus, setScanStatus] = useState<ScanStatus | null>(null)

  useEffect(() => {
    let cancelled = false
    const loadStats = () => {
      fetchStats(token, days)
        .then((data) => !cancelled && setStats(data))
        .catch((err) => !cancelled && setError(err.message))
    }
    loadStats()
    const timer = setInterval(loadStats, 10000)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [token, days])

  useEffect(() => {
    let cancelled = false
    const loadStatus = () => {
      getScanStatus(token)
        .then((data) => !cancelled && setScanStatus(data))
        .catch(() => {})
    }
    loadStatus()
    const timer = setInterval(loadStatus, 5000)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [token])

  return (
    <div className="space-y-6">
      <div className="rounded-2xl bg-gradient-to-r from-cyan-500/20 via-blue-500/10 to-purple-500/10 p-6 ring-1 ring-white/10">
        <p className="text-sm uppercase tracking-[0.18em] text-cyan-200">Host</p>
        <p className="text-3xl font-semibold text-white">{host || 'Unknown'}</p>
        <p className="text-sm text-slate-200">SMB monitoring with automated blocking</p>
      </div>

      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-rose-200">{error}</p>}

      {stats ? (
        <>
          <div className="grid gap-4 md:grid-cols-3">
            <StatCard icon={ShieldCheck} title="Banned IPs" value={stats.totalBanned.toString()} />
            <StatCard
              icon={CircleDot}
              title={`Blocks in the last ${days} days`}
              value={stats.recentBanned.toString()}
              helper="Adjust the window for more context"
            />
            <StatCard icon={Network} title="Window" value={`${days} days`} helper="You can view up to 30 days" />
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            <ScanStatusCard status={scanStatus} />
            <div className="flex items-center gap-3 rounded-xl bg-slate-900/50 p-4 ring-1 ring-white/5">
              <AlarmClock className="text-cyan-300" />
              <div className="text-sm text-slate-300">
                <p className="font-semibold text-white">Timeline window</p>
                <p>Change the window to view attempts over time.</p>
              </div>
            </div>
          </div>
          <div className="flex items-center gap-3 text-sm text-slate-300">
            <span>Time window:</span>
            <select
              value={days}
              onChange={(e) => setDays(parseInt(e.target.value))}
              className="rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
            >
              {[7, 14, 21, 30].map((d) => (
                <option key={d} value={d}>
                  {d} days
                </option>
              ))}
            </select>
          </div>
          <ActivityChart data={stats} />
        </>
      ) : (
        <p className="text-slate-300">Loading statistics...</p>
      )}
    </div>
  )
}

