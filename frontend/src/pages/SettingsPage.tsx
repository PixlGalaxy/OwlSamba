import { useEffect, useState } from 'react'
import { getScanStatus, getSettings, triggerScan, updateSettings } from '../api'
import type { ScanStatus, SettingsResponse } from '../api'
import { SettingsForm } from '../components/SettingsForm'

export function SettingsPage({ token }: { token: string | null }) {
  const [settings, setSettings] = useState<SettingsResponse | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [scanStatus, setScanStatus] = useState<ScanStatus | null>(null)

  const load = () => {
    setLoading(true)
    setError('')
    getSettings(token)
      .then(setSettings)
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false))
  }

  const refreshStatus = () => {
    getScanStatus(token)
      .then(setScanStatus)
      .catch(() => {})
  }

  useEffect(() => {
    load()
    refreshStatus()
    const timer = setInterval(refreshStatus, 5000)
    return () => clearInterval(timer)
  }, [token])

  const handleSave = async (payload: Partial<SettingsResponse>) => {
    const updated = await updateSettings(token, payload)
    setSettings(updated)
    refreshStatus()
  }

  const handleScan = async () => {
    await triggerScan(token)
    refreshStatus()
  }

  if (!settings) return <p className="text-slate-300 text-sm">Loading configuration...</p>

  return (
    <div className="space-y-3 sm:space-y-4">
      {error && <p className="rounded-xl bg-rose-500/10 px-3 sm:px-4 py-2 sm:py-3 text-xs sm:text-sm text-rose-200">{error}</p>}
      <SettingsForm
        settings={settings}
        onSave={handleSave}
        onScan={handleScan}
        loading={loading}
        scanStatus={scanStatus}
      />
    </div>
  )
}

