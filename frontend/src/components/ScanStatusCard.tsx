import { BadgeCheck, Clock4, Loader2, Radar, TimerReset } from 'lucide-react'
import type { ScanStatus } from '../api'

function formatTime(value: string | null) {
  if (!value) return 'Not scheduled'
  const date = new Date(value)
  return `${date.toLocaleDateString()} ${date.toLocaleTimeString()}`
}

export function ScanStatusCard({ status }: { status: ScanStatus | null }) {
  const running = status?.running
  const mode = status?.mode || 'auto'

  return (
    <div className="flex flex-col gap-3 rounded-xl bg-slate-900/50 p-3 sm:p-4 ring-1 ring-white/5">
      <div className="flex items-center gap-2 text-sm sm:text-base font-semibold text-white">
        {running ? <Loader2 className="animate-spin text-cyan-300 flex-shrink-0" size={20} /> : <Radar className="text-cyan-300 flex-shrink-0" size={20} />}
        <span>Scanner status</span>
      </div>
      <div className="grid gap-2 text-xs sm:text-sm text-slate-300 grid-cols-1 lg:grid-cols-2">
        <div className="flex items-start sm:items-center gap-2">
          <BadgeCheck className="text-emerald-300 flex-shrink-0 mt-0.5 sm:mt-0" size={18} />
          <div className="min-w-0">
            <p className="text-white font-medium">{running ? 'Scan in progress' : 'Idle'}</p>
            <p className="text-xs text-slate-400 truncate">{running ? `Mode: ${mode}` : 'Waiting for next cycle'}</p>
          </div>
        </div>
        <div className="flex items-start sm:items-center gap-2">
          <Clock4 className="text-amber-300 flex-shrink-0 mt-0.5 sm:mt-0" size={18} />
          <div className="min-w-0">
            <p className="text-white font-medium">Next scan</p>
            <p className="text-xs text-slate-400 truncate">{formatTime(status?.nextScheduled || null)}</p>
          </div>
        </div>
        <div className="flex items-start sm:items-center gap-2">
          <TimerReset className="text-sky-300 flex-shrink-0 mt-0.5 sm:mt-0" size={18} />
          <div className="min-w-0">
            <p className="text-white font-medium">Last started</p>
            <p className="text-xs text-slate-400 truncate">{formatTime(status?.lastStarted || null)}</p>
          </div>
        </div>
        <div className="flex items-start sm:items-center gap-2">
          <Radar className="text-purple-300 flex-shrink-0 mt-0.5 sm:mt-0" size={18} />
          <div className="min-w-0">
            <p className="text-white font-medium">Last finished</p>
            <p className="text-xs text-slate-400 truncate">{formatTime(status?.lastFinished || null)}</p>
          </div>
        </div>
      </div>
    </div>
  )
}
