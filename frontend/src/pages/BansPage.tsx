import { useEffect, useState } from 'react'
import { addBan, fetchBans, unban } from '../api'
import type { BanFilters, BanRecord } from '../api'
import { BansTable } from '../components/BansTable'

export function BansPage({ token }: { token: string | null }) {
  const [bans, setBans] = useState<BanRecord[]>([])
  const [filters, setFilters] = useState<BanFilters>({})
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const load = () => {
    setLoading(true)
    setError('')
    fetchBans(token, filters)
      .then(setBans)
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false))
  }

  useEffect(() => {
    load()
    const timer = setInterval(load, 10000)
    return () => clearInterval(timer)
  }, [token, filters])

  const handleUnban = async (ip: string) => {
    await unban(token, ip)
    load()
  }

  const handleAdd = async (payload: { ip: string; attempts?: number; workstation?: string; user?: string }) => {
    await addBan(token, payload)
    load()
  }

  return (
    <div className="space-y-4">
      {error && <p className="rounded-xl bg-rose-500/10 px-4 py-3 text-rose-200">{error}</p>}
      <BansTable
        bans={bans}
        filters={filters}
        setFilters={setFilters}
        onRefresh={load}
        onUnban={handleUnban}
        onAdd={handleAdd}
        loading={loading}
      />
    </div>
  )
}

