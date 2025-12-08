import { useMemo } from 'react'
import { BarChart3 } from 'lucide-react'
import type { StatsResponse } from '../api'

export function ActivityChart({ data }: { data: StatsResponse }) {
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
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {data.timeline.map((item) => (
          <div key={item.date} className="space-y-2 rounded-xl bg-slate-900/40 p-3">
            <div className="flex items-center justify-between text-xs text-slate-400">
              <span>{item.date}</span>
              <span className="text-cyan-200">{item.attempts}</span>
            </div>
            <div className="h-12 rounded-lg bg-slate-800/70">
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

