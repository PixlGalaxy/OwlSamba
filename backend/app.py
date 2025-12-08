import importlib.util
import ipaddress
import logging
import os
import secrets
import socket
import sqlite3
import subprocess
import threading
from datetime import datetime, timedelta
from json import dumps as json_dumps
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import bcrypt
from dotenv import load_dotenv
from fastapi import Depends, FastAPI, HTTPException, Request, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from pydantic import BaseModel, Field, validator
from slowapi import Limiter
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded
from fastapi.responses import JSONResponse

BASE_DIR = Path(__file__).resolve().parent
ENV_PATH = BASE_DIR.parent / ".env"

load_dotenv(ENV_PATH)

LOG_DIR = BASE_DIR.parent / "logs"


def setup_logging() -> Tuple[logging.Logger, logging.Logger]:
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    formatter = logging.Formatter(
        fmt="%(asctime)s [%(levelname)s] %(name)s - %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    backend_logger = logging.getLogger("backend")
    backend_logger.setLevel(logging.INFO)
    if not backend_logger.handlers:
        backend_handler = RotatingFileHandler(
            LOG_DIR / "backend.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8"
        )
        backend_handler.setFormatter(formatter)
        backend_logger.addHandler(backend_handler)

    frontend_logger = logging.getLogger("frontend")
    frontend_logger.setLevel(logging.INFO)
    if not frontend_logger.handlers:
        frontend_handler = RotatingFileHandler(
            LOG_DIR / "frontend.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8"
        )
        frontend_handler.setFormatter(formatter)
        frontend_logger.addHandler(frontend_handler)

    return backend_logger, frontend_logger


backend_logger, frontend_logger = setup_logging()

WIN32_AVAILABLE = importlib.util.find_spec("win32evtlog") is not None
if WIN32_AVAILABLE:
    import win32evtlog  # type: ignore[import-not-found]


class AppConfig(BaseModel):
    threshold: int = Field(default=10, ge=1, description="Attempts allowed before banning")
    scan_wait: int = Field(default=5, ge=1, description="Minutes between log scans")
    whitelist_ips: List[str] = Field(default_factory=lambda: ["127.0.0.1"])
    whitelist_domains: List[str] = Field(default_factory=list)
    ban_ips: bool = Field(default=True)
    log_name: str = Field(default="Security")
    event_id: int = Field(default=4625)
    database_file: str = Field(default="DataBase.db")
    hostname: str = Field(default_factory=socket.gethostname)
    dashboard_user: str = Field(default="admin")
    dashboard_password_hash: str = Field(default="")
    allow_local_bypass: bool = Field(
        default=False,
        description="Allow dashboard access without authentication when requests are local",
    )
    discord_webhook_url: Optional[str] = None
    discord_notification_time: int = Field(default=1440)
    allowed_origins: List[str] = Field(default_factory=lambda: ["http://localhost:5173", "http://localhost:3000"])

    @validator("whitelist_ips", pre=True)
    def parse_whitelist_ips(cls, value):
        if isinstance(value, str):
            cleaned = value.replace("\"", "")
            return [ip.strip() for ip in cleaned.split(",") if ip.strip()]
        return value or []

    @validator("whitelist_domains", pre=True)
    def parse_whitelist_domains(cls, value):
        if isinstance(value, str):
            cleaned = value.replace("\"", "")
            return [domain.strip() for domain in cleaned.split(",") if domain.strip()]
        return value or []


class SettingsPayload(BaseModel):
    threshold: int = Field(ge=1)
    scan_wait: int = Field(ge=1)
    ban_ips: bool
    log_name: str
    event_id: int = Field(ge=1)
    whitelist_ips: List[str] = Field(default_factory=list)
    whitelist_domains: List[str] = Field(default_factory=list)


class BanPayload(BaseModel):
    ip: str
    workstation: Optional[str] = None
    user: Optional[str] = None
    attempts: int = Field(default=1, ge=1)


class BanFilter(BaseModel):
    min_attempts: int = 0
    start_date: Optional[datetime] = None
    end_date: Optional[datetime] = None
    sort_by: str = "last_attempt"
    sort_order: str = "desc"


class LoginPayload(BaseModel):
    username: str
    password: str


class SessionManager:
    def __init__(self):
        self._tokens: Dict[str, Tuple[datetime, str]] = {}
        self._lock = threading.Lock()

    def issue_token(self, username: str) -> str:
        token = os.urandom(24).hex()
        expires_at = datetime.now() + timedelta(hours=12)
        with self._lock:
            self._tokens[token] = (expires_at, username)
        return token

    def validate(self, token: str) -> bool:
        now = datetime.now()
        with self._lock:
            stored = self._tokens.get(token)
            if stored and stored[0] > now:
                return True
            if token in self._tokens:
                del self._tokens[token]
        return False

    def revoke(self, token: str) -> None:
        with self._lock:
            self._tokens.pop(token, None)

    def username_for(self, token: str) -> Optional[str]:
        with self._lock:
            stored = self._tokens.get(token)
            if not stored:
                return None
            return stored[1]


def bool_from_env(value: Optional[str], default: bool) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def hash_password(password: str) -> str:
    """Hash a password using bcrypt."""
    salt = bcrypt.gensalt(rounds=12)
    return bcrypt.hashpw(password.encode(), salt).decode()


def verify_password(password: str, hashed: str) -> bool:
    """Verify a password against its hash."""
    try:
        return bcrypt.checkpw(password.encode(), hashed.encode())
    except Exception:
        return False


def serialize_list(values: List[str]) -> str:
    return ", ".join(values)


def update_env_file(updates: Dict[str, str]) -> None:
    existing_lines: List[str] = []
    if ENV_PATH.exists():
        existing_lines = ENV_PATH.read_text(encoding="utf-8").splitlines()

    new_lines: List[str] = []
    remaining = dict(updates)
    for line in existing_lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            new_lines.append(line)
            continue
        if "=" not in line:
            new_lines.append(line)
            continue
        key, _, _ = line.partition("=")
        key = key.strip()
        if key in remaining:
            new_lines.append(f"{key}={remaining.pop(key)}")
        else:
            new_lines.append(line)

    for key, value in remaining.items():
        new_lines.append(f"{key}={value}")

    ENV_PATH.write_text("\n".join(new_lines) + "\n", encoding="utf-8")


def load_config() -> AppConfig:
    password_env = os.getenv("DASHBOARD_PASSWORD", "change_me")
    password_hash_env = os.getenv("DASHBOARD_PASSWORD_HASH", "")
    
    password_hash = password_hash_env
    if not password_hash and password_env:
        password_hash = hash_password(password_env)
        update_env_file({"DASHBOARD_PASSWORD_HASH": password_hash})
    
    return AppConfig(
        threshold=int(os.getenv("THRESHOLD", "10")),
        scan_wait=int(os.getenv("SCAN_WAIT", "5")),
        whitelist_ips=os.getenv("WHITELIST_IPS", "127.0.0.1, 192.168.0.0/24"),
        whitelist_domains=os.getenv("WHITELIST_DOMAINS", ""),
        ban_ips=bool_from_env(os.getenv("BAN_IPS"), True),
        log_name=os.getenv("LOG_NAME", "Security"),
        event_id=int(os.getenv("EVENT_ID", "4625")),
        database_file=os.getenv("DATABASE_FILE", "DataBase.db"),
        hostname=os.getenv("HOSTNAME_OVERRIDE", socket.gethostname()),
        dashboard_user=os.getenv("DASHBOARD_USER", "admin"),
        dashboard_password_hash=password_hash,
        allow_local_bypass=bool_from_env(os.getenv("ALLOW_LOCAL_BYPASS"), False),
        discord_webhook_url=os.getenv("DISCORD_WEBHOOK_URL"),
        discord_notification_time=int(os.getenv("DISCORD_NOTIFICATION_TIME", "1440")),
        allowed_origins=[
            origin.strip() 
            for origin in os.getenv("ALLOWED_ORIGINS", "http://localhost:5173,http://localhost:3000").split(",")
            if origin.strip()
        ],
    )


class DataStore:
    def __init__(self, db_path: Path, config: AppConfig):
        self.db_path = db_path
        self.config = config
        self._lock = threading.Lock()
        self.ensure_schema()

    def _connect(self):
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    def ensure_schema(self):
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as conn:
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS bans (
                    ip TEXT PRIMARY KEY,
                    attempts INTEGER NOT NULL,
                    last_attempt TEXT,
                    workstation TEXT,
                    last_user TEXT,
                    banned INTEGER NOT NULL DEFAULT 0,
                    banned_time TEXT,
                    manual INTEGER NOT NULL DEFAULT 0
                )
                """
            )
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ip TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    workstation TEXT,
                    user TEXT
                )
                """
            )
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT
                )
                """
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_events_time ON events(occurred_at)")
        self.persist_settings()

    def persist_settings(self):
        with self._connect() as conn:
            for key, value in self.config.dict().items():
                if isinstance(value, list):
                    value = ", ".join(value)
                conn.execute(
                    "INSERT OR REPLACE INTO settings(key, value) VALUES(?, ?)",
                    (key, str(value)),
                )

    def load_settings(self) -> AppConfig:
        with self._connect() as conn:
            rows = conn.execute("SELECT key, value FROM settings").fetchall()
        if not rows:
            self.persist_settings()
            return self.config
        loaded = {row["key"]: row["value"] for row in rows}
        self.config = AppConfig(
            threshold=int(loaded.get("threshold", self.config.threshold)),
            scan_wait=int(loaded.get("scan_wait", self.config.scan_wait)),
            whitelist_ips=loaded.get("whitelist_ips", ",".join(self.config.whitelist_ips)),
            whitelist_domains=loaded.get("whitelist_domains", ",".join(self.config.whitelist_domains)),
            ban_ips=bool_from_env(loaded.get("ban_ips"), self.config.ban_ips),
            log_name=loaded.get("log_name", self.config.log_name),
            event_id=int(loaded.get("event_id", self.config.event_id)),
            database_file=loaded.get("database_file", self.config.database_file),
            hostname=loaded.get("hostname", self.config.hostname),
            dashboard_user=loaded.get("dashboard_user", self.config.dashboard_user),
            dashboard_password_hash=loaded.get("dashboard_password_hash", self.config.dashboard_password_hash),
            allow_local_bypass=bool_from_env(
                loaded.get("allow_local_bypass"), self.config.allow_local_bypass
            ),
            discord_webhook_url=loaded.get("discord_webhook_url", self.config.discord_webhook_url),
            discord_notification_time=int(
                loaded.get("discord_notification_time", self.config.discord_notification_time)
            ),
            allowed_origins=[
                origin.strip() for origin in (loaded.get("allowed_origins", "") or "").split(",") if origin.strip()
            ] or self.config.allowed_origins,
        )
        return self.config

    def upsert_ban(
        self,
        ip: str,
        attempts: int,
        last_attempt: datetime,
        workstation: str = "-",
        user: str = "-",
        banned: bool = False,
        manual: bool = False,
    ) -> None:
        with self._lock, self._connect() as conn:
            existing = conn.execute("SELECT attempts FROM bans WHERE ip = ?", (ip,)).fetchone()
            if existing:
                conn.execute(
                    """
                    UPDATE bans
                    SET attempts = ?, last_attempt = ?, workstation = ?, last_user = ?, banned = ?, banned_time = CASE WHEN ? THEN COALESCE(banned_time, ?) ELSE banned_time END, manual = CASE WHEN ? THEN 1 ELSE manual END
                    WHERE ip = ?
                    """,
                    (
                        attempts,
                        last_attempt.isoformat(),
                        workstation,
                        user,
                        int(banned),
                        int(banned),
                        datetime.now().isoformat(),
                        int(manual),
                        ip,
                    ),
                )
            else:
                conn.execute(
                    """
                    INSERT INTO bans(ip, attempts, last_attempt, workstation, last_user, banned, banned_time, manual)
                    VALUES(?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        ip,
                        attempts,
                        last_attempt.isoformat(),
                        workstation,
                        user,
                        int(banned),
                        datetime.now().isoformat() if banned else None,
                        int(manual),
                    ),
                )

    def record_event(
        self,
        ip: str,
        occurred_at: datetime,
        workstation: str = "-",
        user: str = "-",
    ) -> int:
        with self._lock, self._connect() as conn:
            existing = conn.execute(
                "SELECT attempts, banned, last_attempt FROM bans WHERE ip = ?", (ip,)
            ).fetchone()

            existing_last_attempt: Optional[datetime] = None
            attempts = 1
            banned = False

            if existing:
                attempts = existing["attempts"] + 1
                banned = bool(existing["banned"])
                if existing["last_attempt"]:
                    try:
                        existing_last_attempt = datetime.fromisoformat(existing["last_attempt"])
                    except ValueError:
                        existing_last_attempt = None

            if existing_last_attempt and occurred_at <= existing_last_attempt:
                backend_logger.debug(
                    "Skipping duplicate event for %s at %s (last processed %s)",
                    ip,
                    occurred_at.isoformat(),
                    existing_last_attempt.isoformat(),
                )
                return existing["attempts"] if existing else 1

            already_recorded = conn.execute(
                "SELECT 1 FROM events WHERE ip = ? AND occurred_at = ? LIMIT 1",
                (ip, occurred_at.isoformat()),
            ).fetchone()
            if already_recorded:
                backend_logger.debug(
                    "Event already stored for %s at %s; ignoring for counters",
                    ip,
                    occurred_at.isoformat(),
                )
                return existing["attempts"] if existing else 1

            conn.execute(
                "INSERT INTO events(ip, occurred_at, workstation, user) VALUES(?, ?, ?, ?)",
                (ip, occurred_at.isoformat(), workstation, user),
            )
            conn.execute(
                """
                INSERT INTO bans(ip, attempts, last_attempt, workstation, last_user, banned, banned_time, manual)
                VALUES(?, ?, ?, ?, ?, ?, ?, 0)
                ON CONFLICT(ip) DO UPDATE SET
                    attempts = ?,
                    last_attempt = excluded.last_attempt,
                    workstation = excluded.workstation,
                    last_user = excluded.last_user,
                    attempts = excluded.attempts
                """,
                (
                    ip,
                    attempts,
                    occurred_at.isoformat(),
                    workstation,
                    user,
                    int(banned),
                    None,
                    attempts,
                ),
            )
            return attempts

    def set_ban_state(self, ip: str, banned: bool) -> None:
        with self._lock, self._connect() as conn:
            result = conn.execute("SELECT ip FROM bans WHERE ip = ?", (ip,)).fetchone()
            if not result and banned:
                conn.execute(
                    "INSERT INTO bans(ip, attempts, last_attempt, workstation, last_user, banned, banned_time, manual) VALUES(?, 1, ?, '-', '-', 1, ?, 0)",
                    (ip, datetime.now().isoformat(), datetime.now().isoformat()),
                )
            elif result:
                conn.execute(
                    "UPDATE bans SET banned = ?, banned_time = CASE WHEN ? THEN ? ELSE banned_time END WHERE ip = ?",
                    (int(banned), int(banned), datetime.now().isoformat(), ip),
                )

    def ban_state(self, ip: str) -> Tuple[bool, Optional[datetime]]:
        with self._connect() as conn:
            row = conn.execute(
                "SELECT banned, banned_time FROM bans WHERE ip = ?", (ip,)
            ).fetchone()
        if not row:
            return False, None
        banned_time = None
        if row["banned_time"]:
            try:
                banned_time = datetime.fromisoformat(row["banned_time"])
            except ValueError:
                banned_time = None
        return bool(row["banned"]), banned_time

    def unban(self, ip: str) -> None:
        with self._lock, self._connect() as conn:
            conn.execute("UPDATE bans SET banned = 0, banned_time = NULL WHERE ip = ?", (ip,))

    def list_bans(
        self,
        filters: BanFilter,
    ) -> List[Dict[str, str]]:
        query = "SELECT * FROM bans WHERE attempts >= ?"
        params: List[object] = [filters.min_attempts]
        if filters.start_date:
            query += " AND last_attempt >= ?"
            params.append(filters.start_date.isoformat())
        if filters.end_date:
            query += " AND last_attempt <= ?"
            params.append(filters.end_date.isoformat())
        order_field = "last_attempt" if filters.sort_by not in {"attempts", "ip", "banned_time"} else filters.sort_by
        order_dir = "DESC" if filters.sort_order.lower() == "desc" else "ASC"
        query += f" ORDER BY {order_field} {order_dir}"
        with self._connect() as conn:
            rows = conn.execute(query, params).fetchall()
        return [dict(row) for row in rows]

    def stats(self, days: int) -> Dict[str, object]:
        lower_bound = datetime.now() - timedelta(days=days)
        with self._connect() as conn:
            total_banned = conn.execute("SELECT COUNT(*) FROM bans WHERE banned = 1").fetchone()[0]
            recent_banned = conn.execute(
                "SELECT COUNT(*) FROM bans WHERE banned = 1 AND banned_time >= ?",
                (lower_bound.isoformat(),),
            ).fetchone()[0]
            timeline_rows = conn.execute(
                "SELECT occurred_at FROM events WHERE occurred_at >= ? ORDER BY occurred_at",
                (lower_bound.isoformat(),),
            ).fetchall()
        timeline: Dict[str, int] = {}
        for row in timeline_rows:
            date_key = row["occurred_at"][:10]
            timeline[date_key] = timeline.get(date_key, 0) + 1
        return {
            "totalBanned": total_banned,
            "recentBanned": recent_banned,
            "timeline": [
                {"date": day, "attempts": attempts}
                for day, attempts in sorted(timeline.items())
            ],
        }

    def export_bans(self, path: Path) -> None:
        with self._connect() as conn:
            rows = conn.execute("SELECT * FROM bans").fetchall()
        payload = {row["ip"]: dict(row) for row in rows}
        path.write_text(json_dumps(payload), encoding="utf-8")


class SMBScanner:
    def __init__(self, store: DataStore, config: AppConfig):
        self.store = store
        self.config = config
        self.whitelist_cache: List[str] = []

    def resolve_whitelist(self):
        for domain in self.config.whitelist_domains:
            try:
                ip = socket.gethostbyname(domain)
                if ip not in self.config.whitelist_ips:
                    self.config.whitelist_ips.append(ip)
            except Exception:
                continue

    def is_whitelisted(self, ip: str) -> bool:
        try:
            ip_obj = ipaddress.ip_address(ip)
        except ValueError:
            return True
        for white in self.config.whitelist_ips:
            if "/" in white:
                try:
                    if ip_obj in ipaddress.ip_network(white, strict=False):
                        return True
                except ValueError:
                    continue
            else:
                if ip == white:
                    return True
        return False

    def add_to_firewall(self, ip: str):
        if not self.config.ban_ips:
            return
        if os.name == "nt":
            rule_name = f"SMB_block_{ip}"
            subprocess.run(
                [
                    "netsh",
                    "advfirewall",
                    "firewall",
                    "add",
                    "rule",
                    f"name={rule_name}",
                    "dir=in",
                    "action=block",
                    f"remoteip={ip}",
                ],
                check=False,
            )
        backend_logger.info("Firewall rule applied for %s", ip)

    def should_block_ip(self, ip: str, attempts: int) -> bool:
        minimum_attempts = max(10, self.config.threshold)
        if attempts < minimum_attempts:
            return False

        already_banned, banned_time = self.store.ban_state(ip)
        if already_banned:
            if banned_time and datetime.now() - banned_time < timedelta(seconds=60):
                backend_logger.debug(
                    "Skipping re-ban for %s; already banned %ss ago",
                    ip,
                    int((datetime.now() - banned_time).total_seconds()),
                )
            return False
        return True

    def _fetch_events(self):
        if not WIN32_AVAILABLE:
            return []
        server = None
        log_type = win32evtlog.OpenEventLog(server, self.config.log_name)
        events = []
        try:
            flags = win32evtlog.EVENTLOG_FORWARDS_READ | win32evtlog.EVENTLOG_SEQUENTIAL_READ
            while True:
                batch = win32evtlog.ReadEventLog(log_type, flags, 0)
                if not batch:
                    break
                events.extend([event for event in batch if event.EventID == self.config.event_id])
        finally:
            win32evtlog.CloseEventLog(log_type)
        return events

    def _parse_event(self, event) -> Optional[Tuple[str, Dict[str, str]]]:
        try:
            time_generated = event.TimeGenerated.Format()
            ip_address = None
            workstation = "-"
            user = "-"
            if event.StringInserts:
                ip_address = event.StringInserts[-2]
                workstation = event.StringInserts[13]
                user = event.StringInserts[5]
            if ip_address and not self.is_whitelisted(ip_address):
                return ip_address, {
                    "TimeGenerated": time_generated,
                    "Workstation": workstation,
                    "User": user,
                }
        except Exception:
            return None
        return None

    def run_scan(self, mode: str = "manual") -> int:
        if not WIN32_AVAILABLE:
            backend_logger.info("Scan skipped (%s mode); win32evtlog unavailable", mode)
            return 0
        self.resolve_whitelist()
        events = self._fetch_events()
        processed = 0
        backend_logger.info("Scan started (%s mode) with %s events", mode, len(events))
        for event in events:
            parsed = self._parse_event(event)
            if not parsed:
                continue
            ip, details = parsed
            occurred_at = datetime.strptime(details["TimeGenerated"], "%a %b %d %H:%M:%S %Y")
            attempts = self.store.record_event(ip, occurred_at, details["Workstation"], details["User"])
            if self.should_block_ip(ip, attempts):
                self.store.set_ban_state(ip, True)
                self.add_to_firewall(ip)
                backend_logger.info(
                    "Auto-ban applied to %s after %s attempts (user=%s workstation=%s)",
                    ip,
                    attempts,
                    details["User"],
                    details["Workstation"],
                )
            processed += 1
        banned_file = BASE_DIR / "banned_ips.json"
        self.store.export_bans(banned_file)
        backend_logger.info("Scan complete (%s mode): %s events processed", mode, processed)
        return processed


class ScanStatus:
    def __init__(self, scan_wait: int):
        self.running: bool = False
        self.mode: Optional[str] = None
        self.last_started: Optional[datetime] = None
        self.last_finished: Optional[datetime] = None
        self.next_scheduled: Optional[datetime] = datetime.now() + timedelta(minutes=scan_wait)
        self.last_processed: int = 0
        self.scan_wait = scan_wait
        self._lock = threading.Lock()

    def begin(self, mode: str) -> bool:
        with self._lock:
            if self.running:
                return False
            self.running = True
            self.mode = mode
            self.last_started = datetime.now()
            self.last_processed = 0
            return True

    def complete(self, processed: int):
        with self._lock:
            self.running = False
            self.last_finished = datetime.now()
            self.last_processed = processed
            self.next_scheduled = self.last_finished + timedelta(minutes=self.scan_wait)
            self.mode = None

    def update_interval(self, minutes: int):
        with self._lock:
            self.scan_wait = minutes
            now = datetime.now()
            self.next_scheduled = now + timedelta(minutes=minutes)

    def status(self) -> Dict[str, Optional[object]]:
        with self._lock:
            return {
                "running": self.running,
                "mode": self.mode,
                "lastStarted": self.last_started.isoformat() if self.last_started else None,
                "lastFinished": self.last_finished.isoformat() if self.last_finished else None,
                "nextScheduled": self.next_scheduled.isoformat() if self.next_scheduled else None,
                "lastProcessed": self.last_processed,
            }

    def seconds_until_next(self) -> float:
        with self._lock:
            if not self.next_scheduled:
                return float(self.scan_wait * 60)
            delta = (self.next_scheduled - datetime.now()).total_seconds()
            return max(delta, 0)


class ScanScheduler:
    def __init__(self, scanner: SMBScanner, config: AppConfig, status: ScanStatus):
        self.scanner = scanner
        self.config = config
        self.status = status
        self._stop_event = threading.Event()
        self._thread: Optional[threading.Thread] = None

    def _run_wrapper(self, mode: str, already_started: bool = False):
        processed = 0
        if not already_started:
            if not self.status.begin(mode):
                backend_logger.info("Skipping %s scan; another scan is in progress", mode)
                return
        try:
            backend_logger.info("%s scan initiated", mode.title())
            processed = self.scanner.run_scan(mode)
        finally:
            self.status.complete(processed)
            backend_logger.info(
                "%s scan finished (processed=%s). Next scan at %s",
                mode.title(),
                processed,
                self.status.next_scheduled.isoformat() if self.status.next_scheduled else "unscheduled",
            )

    def trigger_manual(self) -> bool:
        if not self.status.begin("manual"):
            return False
        threading.Thread(target=self._run_wrapper, args=("manual", True), daemon=True).start()
        return True

    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()
        backend_logger.info(
            "Automatic scan scheduler started; next scan at %s",
            self.status.next_scheduled.isoformat() if self.status.next_scheduled else "unscheduled",
        )

    def stop(self):
        self._stop_event.set()
        if self._thread:
            self._thread.join(timeout=1)

    def _loop(self):
        while not self._stop_event.is_set():
            wait_seconds = self.status.seconds_until_next()
            if self._stop_event.wait(wait_seconds):
                break
            self._run_wrapper("auto")

    def update_interval(self, minutes: int):
        self.status.update_interval(minutes)
        backend_logger.info("Scan interval updated to %s minutes", minutes)


security_scheme = HTTPBearer(auto_error=False)
sessions = SessionManager()
config = load_config()
store = DataStore(BASE_DIR / config.database_file, config)
scan_status = ScanStatus(config.scan_wait)
scanner = SMBScanner(store, config)
scan_scheduler = ScanScheduler(scanner, config, scan_status)
app = FastAPI(title="OwlSamba API", version="1.0.0")

# Configurar rate limiter
limiter = Limiter(key_func=get_remote_address)
app.state.limiter = limiter


@app.exception_handler(RateLimitExceeded)
def rate_limit_handler(request: Request, exc: RateLimitExceeded):
    """Handle rate limit exceeded errors with a proper JSON response."""
    return JSONResponse(
        status_code=429,
        content={"detail": "Too many requests. Please try again later."},
    )

# Configurar CORS restrictivo
app.add_middleware(
    CORSMiddleware,
    allow_origins=config.allowed_origins,
    allow_credentials=True,
    allow_methods=["GET", "POST"],
    allow_headers=["Content-Type", "Authorization"],
)


@app.on_event("startup")
def ensure_database_schema():
    store.ensure_schema()
    backend_logger.info("Database schema verified and ready")
    scan_scheduler.start()


def request_is_local(request: Request) -> bool:
    forwarded = request.headers.get("x-forwarded-for")
    candidate = forwarded.split(",")[0].strip() if forwarded else request.client.host
    try:
        return ipaddress.ip_address(candidate).is_loopback
    except ValueError:
        return False


def client_ip(request: Request) -> str:
    forwarded = request.headers.get("x-forwarded-for")
    candidate = forwarded.split(",")[0].strip() if forwarded else request.client.host
    return candidate or "unknown"


def require_auth(
    request: Request, credentials: Optional[HTTPAuthorizationCredentials] = Depends(security_scheme)
) -> Optional[str]:
    if config.allow_local_bypass and request_is_local(request):
        return None
    if not credentials or credentials.scheme.lower() != "bearer":
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Authentication required")
    token = credentials.credentials
    if not sessions.validate(token):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid or expired token")
    return token


@app.get("/api/auth/context")
def auth_context(request: Request):
    requires_auth = True
    if config.allow_local_bypass and request_is_local(request):
        requires_auth = False
    return {"requiresAuth": requires_auth, "host": config.hostname}


@app.post("/api/login")
@limiter.limit("5/minute")
def login(payload: LoginPayload, request: Request):
    """Login endpoint with rate limiting (5 attempts per minute)."""
    if not (payload.username and payload.password):
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Missing credentials")
    
    valid_user = config.dashboard_user
    valid_pass_hash = config.dashboard_password_hash
    
    user_matches = payload.username == valid_user
    password_matches = user_matches and verify_password(payload.password, valid_pass_hash)
    
    if not password_matches:
        frontend_logger.warning(
            "Failed login for user '%s' from %s", payload.username, client_ip(request)
        )
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid credentials")
    
    token = sessions.issue_token(payload.username)
    frontend_logger.info("Successful login for user '%s' from %s", payload.username, client_ip(request))
    return {"token": token}


@app.post("/api/logout")
def logout(request: Request, token: Optional[str] = Depends(require_auth)):
    if token:
        username = sessions.username_for(token) or "unknown"
        sessions.revoke(token)
        frontend_logger.info("User '%s' logged out from %s", username, client_ip(request))
    return {"status": "logged_out"}


@app.get("/api/stats")
def get_stats(days: int = 7, token: Optional[str] = Depends(require_auth)):
    days = max(1, min(days, 30))
    data = store.stats(days)
    data.update({"host": config.hostname, "window": days})
    return data


@app.get("/api/bans")
def get_bans(
    min_attempts: int = 0,
    start_date: Optional[str] = None,
    end_date: Optional[str] = None,
    sort_by: str = "last_attempt",
    sort_order: str = "desc",
    token: Optional[str] = Depends(require_auth),
):
    filters = BanFilter(
        min_attempts=min_attempts,
        start_date=datetime.fromisoformat(start_date) if start_date else None,
        end_date=datetime.fromisoformat(end_date) if end_date else None,
        sort_by=sort_by,
        sort_order=sort_order,
    )
    return store.list_bans(filters)


@app.post("/api/bans")
@limiter.limit("30/minute")
def add_ban(payload: BanPayload, request: Request, token: Optional[str] = Depends(require_auth)):
    """Add a manual ban with rate limiting (30 per minute)."""
    try:
        ipaddress.ip_address(payload.ip)
    except ValueError:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Invalid IP")
    now = datetime.now()
    store.upsert_ban(
        payload.ip,
        max(payload.attempts, config.threshold),
        now,
        payload.workstation or "-",
        payload.user or "-",
        banned=True,
        manual=True,
    )
    store.export_bans(BASE_DIR / "banned_ips.json")
    backend_logger.info(
        "Manual ban added for %s (%s attempts) by %s",
        payload.ip,
        max(payload.attempts, config.threshold),
        client_ip(request),
    )
    return {"status": "ok"}


@app.delete("/api/bans/{ip}")
@limiter.limit("30/minute")
def remove_ban(ip: str, request: Request, token: Optional[str] = Depends(require_auth)):
    """Remove a ban with rate limiting (30 per minute)."""
    store.unban(ip)
    store.export_bans(BASE_DIR / "banned_ips.json")
    backend_logger.info("IP %s unbanned by %s", ip, client_ip(request))
    return {"status": "unbanned"}


@app.get("/api/settings")
def get_settings(token: Optional[str] = Depends(require_auth)):
    return store.load_settings().dict()


@app.put("/api/settings")
@limiter.limit("10/minute")
def update_settings(
    payload: SettingsPayload, request: Request, token: Optional[str] = Depends(require_auth)
):
    """Update settings with rate limiting (10 per minute)."""
    config.whitelist_ips = payload.whitelist_ips
    config.whitelist_domains = payload.whitelist_domains
    config.threshold = payload.threshold
    config.scan_wait = payload.scan_wait
    config.ban_ips = payload.ban_ips
    config.log_name = payload.log_name
    config.event_id = payload.event_id
    store.persist_settings()
    scan_scheduler.update_interval(config.scan_wait)
    update_env_file(
        {
            "THRESHOLD": str(config.threshold),
            "SCAN_WAIT": str(config.scan_wait),
            "BAN_IPS": str(config.ban_ips),
            "LOG_NAME": config.log_name,
            "EVENT_ID": str(config.event_id),
            "WHITELIST_IPS": serialize_list(config.whitelist_ips),
            "WHITELIST_DOMAINS": serialize_list(config.whitelist_domains),
        }
    )
    frontend_logger.info(
        "Settings updated by %s: threshold=%s scan_wait=%s event_id=%s",
        client_ip(request),
        config.threshold,
        config.scan_wait,
        config.event_id,
    )
    return config.dict()


@app.post("/api/scan")
def trigger_scan(request: Request, token: Optional[str] = Depends(require_auth)):
    backend_logger.info("Manual scan requested from %s", client_ip(request))
    started = scan_scheduler.trigger_manual()
    if not started:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="Scan already in progress")
    return {"status": "started"}


@app.get("/api/scan/status")
def scan_status_endpoint(token: Optional[str] = Depends(require_auth)):
    return scan_status.status()


@app.get("/api/health")
def healthcheck():
    return {"status": "ok"}
