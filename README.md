# OwlSamba

OwlSamba is a FastAPI-based SMB brute-force monitor with a React/Tailwind dashboard and a tray helper to launch both services. The backend scans Windows security events (or log files) to detect repeated failed logons, tracks attempts in SQLite, and applies firewall blocks when thresholds are exceeded. The frontend provides live stats, scan status, manual scans, ban management, and settings updates (persisted to `.env`).

## Prerequisites
- Python 3.9+ with `pip` (backend, tray)
- Node.js 18+ with `npm` (frontend)
- Windows event log access on Windows
- Optional: firewall permissions to create rules when banning

## Setup
1. **Create your environment file**
   ```bash
   cp .env.example .env
   # edit DASHBOARD_USER, DASHBOARD_PASSWORD, ALLOW_LOCAL_BYPASS, thresholds, ports, etc.
   ```

2. **Install backend dependencies**
   ```bash
   python -m venv .venv
   source .venv/bin/activate  # On Windows: .venv\\Scripts\\activate
   pip install -r backend/requirements.txt
   ```

3. **Install frontend dependencies**
   ```bash
   cd frontend
   npm install
   cd ..
   ```

## Running
### Option A: Individual servers
- **Backend**
  ```bash
  uvicorn backend.app:app --host 0.0.0.0 --port ${API_PORT:-8000}
  ```
  The backend auto-creates the SQLite tables on startup and writes rotating logs to `logs/backend.log`.

- **Frontend (Vite dev server)**
  ```bash
  cd frontend
  npm run dev -- --host 0.0.0.0 --port ${UI_PORT:-5173}
  ```
  The dashboard expects the API at `http://localhost:${API_PORT:-8000}` by default.

### Option B: Tray launcher (Recommended)
Run the tray helper to start both servers and expose an icon with “Open dashboard” and “Exit” actions:
```bash
python backend/tray.py
```
The tray respects `API_PORT` and `UI_PORT` from `.env` and attempts to open the dashboard in your default browser.

## Updates

OwlSamba has an automatic update system that:
- **Checks for new versions** from GitHub releases on startup
- **Protects your data** by preserving `.env`, database, and logs
- **Notifies you** when updates are available
- **One-click installation** from the launcher UI

### How updates work
1. Launcher checks GitHub for new releases
2. If a new version is available, shows "Update Available" button
3. Click to download and apply the update
4. Your configuration and database are automatically preserved
5. Restart to apply changes

### Versioning
- Local version stored in `VERSION` file
- Remote versions checked from GitHub releases (format: `v0.x.x`)
- See `update/README.md` for technical details

## Key features
- Authenticated dashboard with optional localhost bypass toggle
- Automatic and manual scans with real-time status updates and next-scan timing
- Threshold-based banning with cooldown to avoid duplicate firewall rules
- Ban history, manual ban/unban, whitelist management, and settings persistence to `.env`
- Rotating backend (`logs/backend.log`) and frontend (`logs/frontend.log`) logs (last 3 files kept)

## Building frontend for production
```bash
cd frontend
npm run build
# Serve frontend/dist with your preferred static server
```

## Notes
- Ensure `.env` is secured; it contains dashboard credentials and tuning values.
- When running on Windows, launch with privileges sufficient to read Security logs and create firewall rules if banning is enabled.