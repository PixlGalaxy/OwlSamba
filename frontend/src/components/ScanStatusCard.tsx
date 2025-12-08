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
    <div className="flex flex-col gap-3 rounded-xl bg-slate-900/50 p-4 ring-1 ring-white/5">
      <div className="flex items-center gap-2 text-sm font-semibold text-white">
        {running ? <Loader2 className="animate-spin text-cyan-300" /> : <Radar className="text-cyan-300" />}
        <span>Scanner status</span>
      </div>
      <div className="grid gap-2 text-sm text-slate-300 md:grid-cols-2">
        <div className="flex items-center gap-2">
          <BadgeCheck className="text-emerald-300" />
          <div>
            <p className="text-white">{running ? 'Scan in progress' : 'Idle'}</p>
            <p className="text-xs text-slate-400">{running ? `Mode: ${mode}` : 'Waiting for next cycle'}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Clock4 className="text-amber-300" />
          <div>
            <p className="text-white">Next scan</p>
            <p className="text-xs text-slate-400">{formatTime(status?.nextScheduled || null)}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <TimerReset className="text-sky-300" />
          <div>
            <p className="text-white">Last started</p>
            <p className="text-xs text-slate-400">{formatTime(status?.lastStarted || null)}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Radar className="text-purple-300" />
          <div>
            <p className="text-white">Last finished</p>
            <p className="text-xs text-slate-400">{formatTime(status?.lastFinished || null)}</p>
          </div>
        </div>
      </div>
    </div>
  )
}
