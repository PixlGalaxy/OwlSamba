import { useEffect, useState } from 'react'
import { getSettings, SettingsResponse, triggerScan, updateSettings } from '../api'
import { SettingsForm } from '../components/SettingsForm'

export function SettingsPage({ token }: { token: string | null }) {
  const [settings, setSettings] = useState<SettingsResponse | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)

  const load = () => {
    setLoading(true)
    setError('')
    getSettings(token)
      .then(setSettings)
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false))
  }

  useEffect(() => {
    load()
  }, [token])

  const handleSave = async (payload: Partial<SettingsResponse>) => {
    const updated = await updateSettings(token, payload)
    setSettings(updated)
  }

  const handleScan = async () => {
    await triggerScan(token)
  }

  if (!settings) return <p className="text-slate-300">Cargando configuración...</p>

  return (
    <div className="space-y-4">
      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-rose-200">{error}</p>}
      <SettingsForm settings={settings} onSave={handleSave} onScan={handleScan} loading={loading} />
    </div>
  )
}

