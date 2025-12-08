import { useMemo } from 'react'
import { BarChart3 } from 'lucide-react'
import type { StatsResponse } from '../api'

export function ActivityChart({ data }: { data: StatsResponse }) {
  const max = useMemo(() => Math.max(...data.timeline.map((t) => t.attempts), 1), [data])

  return (
    <div className="rounded-2xl bg-card/70 p-4 sm:p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="mb-4 flex items-start sm:items-center gap-3 text-xs sm:text-sm text-slate-300">
        <BarChart3 className="text-cyan-300 flex-shrink-0" size={20} />
        <div className="min-w-0">
          <p className="text-xs uppercase tracking-[0.18em] text-slate-400">Activity</p>
          <p className="font-semibold text-white">Blocked attempts (last {data.window} days)</p>
        </div>
      </div>
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-2 sm:gap-3">
        {data.timeline.map((item) => (
          <div key={item.date} className="space-y-2 rounded-xl bg-slate-900/40 p-2 sm:p-3">
            <div className="flex items-center justify-between text-xs text-slate-400">
              <span className="truncate">{item.date}</span>
              <span className="text-cyan-200 flex-shrink-0">{item.attempts}</span>
            </div>
            <div className="h-10 sm:h-12 rounded-lg bg-slate-800/70">
              <div
                className="h-full rounded-lg bg-gradient-to-r from-cyan-500 to-blue-500"
                style={{ width: `${Math.round((item.attempts / max) * 100)}%` }}
              />
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

