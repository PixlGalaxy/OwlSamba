import { useState } from 'react'
import { useLocation, Link } from 'react-router-dom'
import { Home, ShieldOff, Settings, ShieldCheck, LogOut, Menu, X } from 'lucide-react'

export function Navigation({ onLogout }: { onLogout: () => Promise<void> | void }) {
  const location = useLocation()
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  
  const links = [
    { to: '/', label: 'Home', icon: Home },
    { to: '/bans', label: 'Banned IPs', icon: ShieldOff },
    { to: '/settings', label: 'Settings', icon: Settings },
  ]

  const handleLinkClick = () => {
    setMobileMenuOpen(false)
  }

  return (
    <nav className="rounded-2xl bg-card/70 shadow-lg shadow-black/20 ring-1 ring-white/5">
      <div className="flex items-center justify-between p-4">
        <div className="flex items-center gap-3">
          <ShieldCheck className="text-cyan-300" size={20} />
          <div className="hidden sm:block">
            <p className="text-xs sm:text-sm text-slate-400">OwlSamba</p>
            <p className="text-sm sm:text-base font-semibold text-slate-100">SMB Guardian</p>
          </div>
        </div>

        {/* Desktop Navigation */}
        <div className="hidden md:flex items-center gap-2 text-sm font-medium">
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
                <span className="hidden lg:inline">{link.label}</span>
              </Link>
            )
          })}
          <button
            onClick={() => onLogout?.()}
            className="flex items-center gap-2 rounded-xl px-4 py-2 text-slate-300 transition hover:bg-white/5"
          >
            <LogOut size={18} />
            <span className="hidden lg:inline">Logout</span>
          </button>
        </div>

        {/* Mobile Menu Button */}
        <button
          onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
          className="md:hidden p-2 rounded-lg text-slate-300 hover:bg-white/5 transition"
        >
          {mobileMenuOpen ? <X size={24} /> : <Menu size={24} />}
        </button>
      </div>

      {/* Mobile Menu */}
      {mobileMenuOpen && (
        <div className="md:hidden border-t border-white/5 bg-card/50 p-3 space-y-2">
          {links.map((link) => {
            const active = location.pathname === link.to
            const Icon = link.icon
            return (
              <Link
                key={link.to}
                to={link.to}
                onClick={handleLinkClick}
                className={`flex items-center gap-3 rounded-lg px-4 py-2.5 transition-all ${
                  active
                    ? 'bg-cyan-500/20 text-cyan-100 ring-1 ring-cyan-400/70'
                    : 'text-slate-300 hover:bg-white/5'
                }`}
              >
                <Icon size={20} />
                <span className="text-sm font-medium">{link.label}</span>
              </Link>
            )
          })}
          <button
            onClick={() => {
              onLogout?.()
              handleLinkClick()
            }}
            className="w-full flex items-center gap-3 rounded-lg px-4 py-2.5 text-slate-300 hover:bg-white/5 transition"
          >
            <LogOut size={20} />
            <span className="text-sm font-medium">Logout</span>
          </button>
        </div>
      )}
    </nav>
  )
}

