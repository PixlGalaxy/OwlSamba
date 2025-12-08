import { useLocation, Link } from 'react-router-dom'
import { Home, ShieldOff, Settings, ShieldCheck, LogOut } from 'lucide-react'

export function Navigation({ onLogout }: { onLogout: () => void }) {
  const location = useLocation()
  const links = [
    { to: '/', label: 'Home', icon: Home },
    { to: '/bans', label: 'Banned IPs', icon: ShieldOff },
    { to: '/settings', label: 'Settings', icon: Settings },
  ]

  return (
    <nav className="flex items-center justify-between rounded-2xl bg-card/70 p-4 shadow-lg shadow-black/20 ring-1 ring-white/5">
      <div className="flex items-center gap-3">
        <ShieldCheck className="text-cyan-300" />
        <div>
          <p className="text-sm text-slate-400">OwlSamba</p>
          <p className="font-semibold text-slate-100">SMB Guardian</p>
        </div>
      </div>
      <div className="flex items-center gap-2 text-sm font-medium">
        {links.map((link) => {
          const active = location.pathname === link.to
          const Icon = link.icon
          return (
            <Link
              key={link.to}
              to={link.to}
              className={`flex items-center gap-2 rounded-xl px-4 py-2 transition-all ${
                active
                  ? 'bg-cyan-500/20 text-cyan-100 ring-1 ring-cyan-400/70'
                  : 'text-slate-300 hover:bg-white/5'
              }`}
            >
              <Icon size={18} />
              {link.label}
            </Link>
          )
        })}
        <button
          onClick={onLogout}
          className="flex items-center gap-2 rounded-xl px-4 py-2 text-slate-300 transition hover:bg-white/5"
        >
          <LogOut size={18} />
          Logout
        </button>
      </div>
    </nav>
  )
}

