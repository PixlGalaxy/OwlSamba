import { FormEvent, useState } from 'react'
import { Loader2, ScanEye, Settings, ShieldCheck } from 'lucide-react'
import type { ScanStatus, SettingsResponse } from '../api'

export function SettingsForm({
  settings,
  onSave,
  onScan,
  loading,
  scanStatus,
}: {
  settings: SettingsResponse
  onSave: (payload: Partial<SettingsResponse>) => Promise<void>
  onScan: () => Promise<void>
  loading: boolean
  scanStatus: ScanStatus | null
}) {
  const [local, setLocal] = useState(settings)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const running = scanStatus?.running
  const modeLabel = running ? `${scanStatus?.mode || 'auto'} scan running` : 'Scanner idle'
  const next = scanStatus?.nextScheduled ? new Date(scanStatus.nextScheduled) : null
  const last = scanStatus?.lastFinished ? new Date(scanStatus.lastFinished) : null

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setMessage('')
    setError('')
    try {
      await onSave(local)
      setMessage('Settings saved')
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={handleSubmit} className="rounded-2xl bg-card/70 p-5 ring-1 ring-white/5 shadow-lg shadow-black/20">
      <div className="mb-4 flex items-center gap-3 text-slate-200">
        <Settings className="text-cyan-300" />
        <div>
          <p className="text-xs uppercase tracking-[0.18em] text-slate-400">Settings</p>
          <p className="font-semibold text-white">Blocker parameters</p>
        </div>
      </div>
      {message && <p className="mb-2 rounded-lg bg-emerald-500/10 px-3 py-2 text-sm text-emerald-200">{message}</p>}
      {error && <p className="mb-2 rounded-lg bg-rose-500/10 px-3 py-2 text-sm text-rose-200">{error}</p>}

      <div className="grid gap-4 md:grid-cols-2">
        <Field label="Attempt threshold">
          <input
            type="number"
            value={local.threshold}
            onChange={(e) => setLocal({ ...local, threshold: parseInt(e.target.value) })}
            className="w-full rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
        </Field>
        <Field label="Scan frequency (minutes)">
          <input
            type="number"
            value={local.scan_wait}
            onChange={(e) => setLocal({ ...local, scan_wait: parseInt(e.target.value) })}
            className="w-full rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
        </Field>
        <Field label="Apply firewall block">
          <input
            type="checkbox"
            checked={local.ban_ips}
            onChange={(e) => setLocal({ ...local, ban_ips: e.target.checked })}
            className="h-4 w-4 accent-cyan-400"
          />
        </Field>
        <Field label="Log name">
          <input
            value={local.log_name}
            onChange={(e) => setLocal({ ...local, log_name: e.target.value })}
            className="w-full rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
        </Field>
        <Field label="Event ID">
          <input
            type="number"
            value={local.event_id}
            onChange={(e) => setLocal({ ...local, event_id: parseInt(e.target.value) })}
            className="w-full rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
        </Field>
        <Field label="Whitelisted IPs (comma-separated)">
          <input
            value={local.whitelist_ips.join(',')}
            onChange={(e) => setLocal({ ...local, whitelist_ips: e.target.value.split(',').map((s) => s.trim()).filter(Boolean) })}
            className="w-full rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
        </Field>
        <Field label="Whitelisted domains (comma-separated)">
          <input
            value={local.whitelist_domains.join(',')}
            onChange={(e) =>
              setLocal({ ...local, whitelist_domains: e.target.value.split(',').map((s) => s.trim()).filter(Boolean) })
            }
            className="w-full rounded-lg bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
          />
        </Field>
      </div>

      <div className="mt-4 flex flex-wrap gap-3">
        <button
          disabled={saving}
          className="flex items-center gap-2 rounded-lg bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-2 font-semibold text-white shadow-cyan-500/20 transition hover:from-cyan-400 hover:to-blue-400 disabled:opacity-60"
        >
          <ShieldCheck size={18} /> Save
        </button>
        <button
          type="button"
          disabled={loading || running}
          onClick={onScan}
          className="rounded-lg bg-slate-800/70 px-4 py-2 text-sm text-slate-200 ring-1 ring-white/10 transition hover:bg-slate-700 disabled:opacity-60"
        >
          {running ? (
            <span className="flex items-center gap-2">
              <Loader2 className="h-4 w-4 animate-spin" />
              Running...
            </span>
          ) : (
            'Run scan'
          )}
        </button>
      </div>

      <div className="mt-3 flex items-center gap-3 rounded-xl bg-slate-900/50 p-4 text-sm text-slate-200 ring-1 ring-white/10">
        <ScanEye className="text-cyan-300" />
        <div>
          <p className="font-semibold text-white">{modeLabel}</p>
          <p className="text-xs text-slate-400">
            {running
              ? 'Scanning now...'
              : `Next automatic scan ${next ? `at ${next.toLocaleTimeString()}` : 'is being scheduled'}`}
          </p>
          {last && !running && <p className="text-xs text-slate-400">Last finished: {last.toLocaleString()}</p>}
        </div>
      </div>
    </form>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-sm text-slate-300">
      <span>{label}</span>
      {children}
    </label>
  )
}

