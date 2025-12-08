# Security Features & Improvements

OwlSamba has been enhanced with enterprise-grade security features to make it safe for public deployment.

## Security Enhancements

### 1. **Password Hashing with bcrypt**
- Passwords are now hashed using bcrypt (12 rounds) and stored in the database
- The `DASHBOARD_PASSWORD_HASH` is automatically generated on first run
- Even if your `.env` file is leaked, the password hash cannot be reversed

**How it works:**
- First time: You set `DASHBOARD_PASSWORD=change_me_securely` in `.env`
- The app automatically hashes it and saves to `DASHBOARD_PASSWORD_HASH`
- Remove or clear `DASHBOARD_PASSWORD` after first run for extra security

### 2. **Rate Limiting**
- Login endpoint: **5 attempts per minute** (prevents brute force)
- Ban management: **30 operations per minute**
- Settings updates: **10 changes per minute**
- Uses slowapi for distributed rate limiting

**Attack prevention:**
- Attackers can't brute force passwords
- Prevents accidental API abuse
- Returns 429 (Too Many Requests) when exceeded

### 3. **Restricted CORS**
- **Before:** `allow_origins=["*"]` - Any website could access your API
- **After:** Only specified domains in `ALLOWED_ORIGINS`
- **Default:** `http://localhost:5173,http://localhost:3000`

**For production:**
```
ALLOWED_ORIGINS=https://yourdomain.com,https://dashboard.yourdomain.com
```

### 4. **Limited HTTP Methods**
- **Before:** `allow_methods=["*"]` - All HTTP methods allowed
- **After:** Only `GET` and `POST` allowed
- Reduces attack surface for OPTIONS, HEAD, TRACE, etc.

### 5. **Limited Headers**
- **Before:** `allow_headers=["*"]` - Any header accepted
- **After:** Only `Content-Type` and `Authorization` allowed
- Prevents header injection attacks

### 6. **Improved Logging**
- Failed login attempts are logged with IP address
- All sensitive operations (bans, settings changes) are audited
- Clear distinction between frontend and backend logs

---

## Installation & Setup

### 1. Install new dependencies
```bash
pip install -r backend/requirements.txt
```

New packages:
- `bcrypt` - Password hashing
- `slowapi` - Rate limiting

### 2. Configure your `.env` file

**Development:**
```env
DASHBOARD_USER=admin
DASHBOARD_PASSWORD=change_me_securely
ALLOW_LOCAL_BYPASS=False
ALLOWED_ORIGINS=http://localhost:5173,http://localhost:3000
```

**Production:**
```env
DASHBOARD_USER=your_username
DASHBOARD_PASSWORD=strong_secure_password_here
ALLOW_LOCAL_BYPASS=False
ALLOWED_ORIGINS=https://yourdomain.com,https://dashboard.yourdomain.com
```

### 3. First run
- The app will automatically hash `DASHBOARD_PASSWORD` and save it as `DASHBOARD_PASSWORD_HASH`
- You can then remove `DASHBOARD_PASSWORD` from `.env` for extra security (optional)

---

## Security Best Practices

### Do:
- Use a strong, unique password (12+ chars, mixed case, numbers, symbols)
- Set `ALLOW_LOCAL_BYPASS=False` for public deployments
- Use HTTPS/TLS in production (not just HTTP)
- Restrict `ALLOWED_ORIGINS` to your domain only
- Rotate the password periodically
- Keep dependencies updated (`pip install -U -r backend/requirements.txt`)
- Monitor logs for failed login attempts

### Don't:
- Set `ALLOWED_ORIGINS=*` in production
- Use weak passwords like "admin" or "123456"
- Enable `ALLOW_LOCAL_BYPASS` in production
- Share your `.env` file
- Run without HTTPS in production
- Keep old password hashes in version control

---

## How Authentication Works

```
User Login
    ↓
1. Request: POST /api/login with username + password
2. Rate limit check: Max 5 per minute per IP
3. Password verification: bcrypt.checkpw(input, hash) 
4. Token generation: Secure 24-byte token (48 hex chars)
5. Token expiration: 12 hours
6. Response: {"token": "..."} or 401 Unauthorized
    ↓
User accesses API
    ↓
1. Request: Include "Authorization: Bearer {token}"
2. Token validation: Check expiration & existence
3. Allow/Deny access to protected endpoints
```

---

## Rate Limiting Details

| Endpoint | Limit | Purpose |
|----------|-------|---------|
| `POST /api/login` | 5/min | Prevent brute force |
| `POST /api/bans` | 30/min | Prevent accidental bulk operations |
| `DELETE /api/bans/{ip}` | 30/min | Prevent accidental bulk operations |
| `PUT /api/settings` | 10/min | Prevent config thrashing |
| `GET /api/stats` | Unlimited | Read-only, safe |
| `GET /api/bans` | Unlimited | Read-only, safe |

---

## Troubleshooting

**Q: "Invalid credentials" on first login?**
- A: Wait a moment - the hash is being generated. Try again after a few seconds.

**Q: Rate limited - can't login?**
- A: Wait 1 minute or use another IP. Rate limits reset every minute.

**Q: CORS error when accessing from browser?**
- A: Add your domain to `ALLOWED_ORIGINS` in `.env`

**Q: "Too many requests" error?**
- A: You've exceeded the rate limit for that endpoint. Wait and retry.

---

## Migration from Old Version

If upgrading from previous versions:

1. Install new packages: `pip install bcrypt slowapi`
2. The app will automatically migrate:
   - Read `DASHBOARD_PASSWORD` from `.env`
   - Generate hash using bcrypt
   - Save as `DASHBOARD_PASSWORD_HASH`
3. You can optionally remove `DASHBOARD_PASSWORD` from `.env`

---

## Firewall & Ban Features

All security improvements are **non-intrusive**:
- Firewall blocking works exactly as before
- Ban management endpoints are protected with rate limiting + auth
- SMB scanning is unchanged
- Event processing is unchanged

---

## Support

For security vulnerabilities, please report privately instead of opening public issues.

Questions about the security implementation? Check:
- `backend/app.py` - See `hash_password()`, `verify_password()`, and rate limiter setup
- The logging shows all authentication events
