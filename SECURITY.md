# Security

## Reporting a vulnerability

Please open a private security advisory on GitHub, or contact the maintainer directly. Don't file a public issue for vulnerabilities.

## Security design (v2)

### Protection engine
- Runs as the **LocalSystem** Windows service, independent of any signed-in user.
- Reads failed logons the moment Windows writes them (`EventLogWatcher`), with no polling window, so an attacker gets at most the configured number of tries (default 3) before being blocked.
- After a ban, it closes the attacker's open SMB sessions, in case a guess already succeeded.
- When an IP with recent failures then logs on successfully, it raises an alert and can optionally ban that IP.
- Never bans loopback, the server's own addresses or whitelisted entries, so you can't lock yourself out of the server from the server itself.

### Desktop app ↔ service
- The service listens on **127.0.0.1 only**, on a random port, and accepts only requests from loopback that carry a random 256-bit key.
- That key is stored in `C:\ProgramData\OwlSamba\local-endpoint.json`, readable only by **Administrators and SYSTEM**. The desktop app requires elevation for the same reason.

### Web dashboard
- Served over **HTTPS** with an automatically generated self-signed certificate (RSA 2048, private key file restricted to Administrators/SYSTEM). To use your own certificate, replace `dashboard.pfx` (passwordless PKCS#12) in the data folder.
- Passwords are hashed with **PBKDF2-SHA256** (210,000 iterations). Bcrypt hashes migrated from v1 are accepted once and upgraded on the next login.
- Sessions use **HttpOnly, Secure, SameSite=Strict** cookies and expire after 12 hours. Changing the password or username invalidates all sessions.
- **CSRF:** every state-changing request must carry a custom header, which browsers won't send cross-origin.
- **Brute force:** failed dashboard logins feed the same engine as SMB, so 3 failures in 60 s get the IP blocked at the firewall.
- Strict **Content-Security-Policy**, `X-Frame-Options: DENY` and `nosniff`. All attacker-controlled text (user names, workstation names) is HTML-escaped before rendering.
- If no password has been set, nobody can sign in at all.

### Data
- Everything lives in `C:\ProgramData\OwlSamba`. The service locks that folder down to **Administrators and SYSTEM** (ProgramData normally lets standard users create files in it), so nobody can plant a certificate or database there.
- In CSV exports, cells that start with `= + - @` are prefixed with `'` to neutralize spreadsheet formula injection.
- Restoring a backup validates the file and snapshots the current database first.

### Privacy
- With GeoIP enabled, attacking **public** IPs are sent to ip-api.com to look up their country and ISP. Private addresses are labeled locally. You can disable this in Settings.
