import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import './App.css'
import './index.css'
import { Navigation } from './components/Navigation'
import { LoginView } from './components/LoginView'
import { useAuthContext } from './hooks/useAuthContext'
import { BansPage } from './pages/BansPage'
import { HomePage } from './pages/HomePage'
import { SettingsPage } from './pages/SettingsPage'

function ProtectedRoute({
  children,
  token,
  requiresAuth,
}: {
  children: JSX.Element
  token: string | null
  requiresAuth: boolean
}) {
  if (!requiresAuth || token) return children
  return <Navigate to="/login" replace />
}

function AppRouter() {
  const auth = useAuthContext()

  if (auth.error) {
    return (
      <div className="app-shell flex min-h-screen items-center justify-center bg-surface px-4">
        <div className="rounded-2xl bg-card/70 p-6 text-center text-rose-200 ring-1 ring-white/5">{auth.error}</div>
      </div>
    )
  }

  if (!auth.context) {
    return (
      <div className="app-shell flex min-h-screen items-center justify-center bg-surface px-4 text-slate-300">
        Cargando contexto...
      </div>
    )
  }

  return (
    <Routes>
      <Route
        path="/login"
        element={<LoginView onAuthenticated={auth.saveToken} requiresAuth={auth.context.requiresAuth} />}
      />
      <Route
        path="/"
        element={
          <ProtectedRoute token={auth.token} requiresAuth={auth.context.requiresAuth}>
            <Layout auth={auth}>
              <HomePage token={auth.token} host={auth.context.host} />
            </Layout>
          </ProtectedRoute>
        }
      />
      <Route
        path="/bans"
        element={
          <ProtectedRoute token={auth.token} requiresAuth={auth.context.requiresAuth}>
            <Layout auth={auth}>
              <BansPage token={auth.token} />
            </Layout>
          </ProtectedRoute>
        }
      />
      <Route
        path="/settings"
        element={
          <ProtectedRoute token={auth.token} requiresAuth={auth.context.requiresAuth}>
            <Layout auth={auth}>
              <SettingsPage token={auth.token} />
            </Layout>
          </ProtectedRoute>
        }
      />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

function Layout({ children, auth }: { children: React.ReactNode; auth: ReturnType<typeof useAuthContext> }) {
  return (
    <div className="app-shell min-h-screen bg-surface px-4 py-6 text-slate-100">
      <div className="mx-auto flex max-w-6xl flex-col gap-6">
        <Navigation onLogout={() => auth.saveToken(null)} />
        {children}
      </div>
    </div>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AppRouter />
    </BrowserRouter>
  )
}

