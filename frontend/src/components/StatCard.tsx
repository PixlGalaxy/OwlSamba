import type { ElementType } from 'react'

export function StatCard({ icon: Icon, title, value, helper }: { icon: ElementType; title: string; value: string; helper?: string }) {
  return (
    <div className="rounded-2xl bg-card/70 p-4 sm:p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="flex items-start sm:items-center justify-between gap-3">
        <div className="min-w-0 flex-1">
          <p className="text-xs uppercase tracking-wide text-slate-400">{title}</p>
          <p className="text-2xl sm:text-3xl font-semibold text-white">{value}</p>
          {helper && <p className="text-xs text-slate-500 mt-1">{helper}</p>}
        </div>
        <div className="rounded-xl bg-cyan-500/10 p-2.5 sm:p-3 text-cyan-300 flex-shrink-0">
          <Icon size={20} className="sm:w-6 sm:h-6" />
        </div>
      </div>
    </div>
  )
}

