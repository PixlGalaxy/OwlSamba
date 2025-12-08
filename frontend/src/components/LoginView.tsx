import { FormEvent, useState } from 'react'
import { LockKeyhole, ShieldCheck } from 'lucide-react'
import { login } from '../api'

export function LoginView({
  onAuthenticated,
  requiresAuth,
}: {
  onAuthenticated: (token: string) => void
  requiresAuth: boolean
}) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setError('')
    try {
      const token = await login(username, password)
      onAuthenticated(token)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="app-shell flex min-h-screen items-center justify-center bg-surface px-4">
      <div className="w-full max-w-md rounded-3xl bg-card/70 p-8 shadow-2xl shadow-black/30 ring-1 ring-white/5">
        <div className="mb-6 flex items-center gap-3">
          <LockKeyhole className="text-cyan-300" />
          <div>
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Secure Access</p>
            <p className="text-lg font-semibold text-white">Sign in to continue</p>
          </div>
        </div>
        {error && <p className="mb-4 rounded-xl bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{error}</p>}
        {requiresAuth ? null : (
          <p className="mb-3 rounded-xl bg-emerald-500/10 px-3 py-2 text-sm text-emerald-200">
            Estás en localhost: autenticación deshabilitada.
          </p>
        )}
        <form className="space-y-4" onSubmit={submit}>
          <div>
            <label className="text-sm text-slate-300">Usuario</label>
            <input
              className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
              autoComplete="username"
            />
          </div>
          <div>
            <label className="text-sm text-slate-300">Contraseña</label>
            <input
              type="password"
              className="mt-1 w-full rounded-xl bg-slate-900/60 px-3 py-2 text-white ring-1 ring-white/10 focus:outline-none focus:ring-2 focus:ring-cyan-400"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              autoComplete="current-password"
            />
          </div>
          <button
            disabled={loading}
            className="flex w-full items-center justify-center gap-2 rounded-xl bg-gradient-to-r from-cyan-500 to-blue-500 px-4 py-3 font-semibold text-white shadow-lg shadow-cyan-500/20 hover:from-cyan-400 hover:to-blue-400 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <ShieldCheck size={18} /> {loading ? 'Verificando...' : 'Entrar'}
          </button>
        </form>
        <p className="mt-4 text-center text-xs text-slate-500">
          El panel solo requiere inicio de sesión cuando no estás en localhost.
        </p>
      </div>
    </div>
  )
}

