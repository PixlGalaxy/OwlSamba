import type { ElementType } from 'react'

export function StatCard({ icon: Icon, title, value, helper }: { icon: ElementType; title: string; value: string; helper?: string }) {
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

