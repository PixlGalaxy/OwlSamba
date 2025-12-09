# OwlSamba

OwlSamba is a SMB brute-force monitor with a React/Tailwind dashboard and service launchers to run both servers. The backend scans Windows security events (or log files) to detect repeated failed logons, tracks attempts in SQLite, and applies firewall blocks when thresholds are exceeded. The frontend provides live stats, scan status, manual scans, ban management, and settings updates (persisted to .env).

## Prerequisites
- **Python 3.9+** with pip (backend, service)
- **Node.js 18+** with 
pm (frontend)
- Windows event log access on Windows
- Optional: firewall permissions to create rules when banning

> ⚠️ **Important:** Make sure you have both Python and Node.js installed and available in your PATH before proceeding.

## Setup

### 1. Configure environment variables
First, rename the example environment file and configure it with your settings:

`bash
# Rename the example file
mv .env.example .env
# Or on Windows:
# ren .env.example .env
`

Then edit the .env file with your desired settings:
- DASHBOARD_USER - Username for dashboard login
- DASHBOARD_PASSWORD - Password for dashboard login
- ALLOW_LOCAL_BYPASS - Allow connections from localhost without authentication (True/False)
- API_PORT - Backend API port (default: 8000)
- UI_PORT - Frontend UI port (default: 5173)
- FAILED_ATTEMPTS_THRESHOLD - Number of failed login attempts before banning on OwlSamba (default: 5)
- BAN_DURATION_MINUTES - How long to ban an IP on OwlSamba (default: 30)
- Other threshold and configuration values

### 2. Install backend dependencies
Navigate to the backend folder and install Python dependencies:

`bash
cd backend
pip install -r requirements.txt
cd ..
`

### 3. Install frontend dependencies
Navigate to the frontend folder and install Node.js dependencies:

`bash
cd frontend
npm install
cd ..
`

## Running

### Option A: Without terminal (Recommended)
Run the service without showing a terminal window:
`bash
python backend/service.pyw
`
This launches both the backend and frontend servers in the background. The application will open automatically in the tray area.

### Option B: With terminal (for debugging)
Run the service with a terminal window visible for debugging:
`bash
python backend/service.py
`
This is useful for viewing console output and logs while developing or troubleshooting.

### Option C: Individual servers (Development)
- **Backend**
  `bash
  uvicorn backend.app:app --host 0.0.0.0 --port \$\{API_PORT:-8000\}
  `
  The backend auto-creates the SQLite tables on startup and writes rotating logs to logs/backend.log.

- **Frontend (Vite dev server)**
  `bash
  cd frontend
  npm run dev -- --host 0.0.0.0 --port \$\{UI_PORT:-5173\}
  `
  The dashboard expects the API at http://localhost:\$\{API_PORT:-8000\} by default.

## Key features
- Authenticated dashboard with optional localhost bypass toggle
- Automatic and manual scans with real-time status updates and next-scan timing
- Threshold-based banning with cooldown to avoid duplicate firewall rules
- Ban history, manual ban/unban, whitelist management, and settings persistence to .env
- Application logging (logs/backend.log) and frontend (logs/frontend.log) logs

## Building frontend for production
`bash
cd frontend
npm run build

## Notes
- Ensure .env is secured; it contains dashboard credentials and tuning values.
- When running on Windows, launch with privileges sufficient to read Security logs and create firewall rules if banning is enabled.
