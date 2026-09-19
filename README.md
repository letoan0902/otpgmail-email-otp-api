# OTPGmail – Email OTP API with real Gmail & iCloud inboxes

**Receive email verification codes (OTP) programmatically.** Rent a real `@gmail.com` or `@icloud.com` inbox for
one sign-up, read the code over a tiny REST API, and get an automatic refund when no code arrives.

[Website](https://otpgmail.net/?utm_source=github&utm_medium=readme&utm_campaign=en) ·
[API reference](https://otpgmail.net/app/docs?utm_source=github&utm_medium=readme&utm_campaign=en) ·
[Live prices & stock](https://otpgmail.net/app/services?utm_source=github&utm_medium=readme&utm_campaign=en) ·
[Tiếng Việt](https://github.com/letoan0902/thue-otp-gmail)

This repository contains small, dependency-light clients and examples in **Python, Node.js, PHP, C# (.NET) and
curl**. Every example here was run end-to-end against the production API before publishing.

## Why not a disposable-mail domain?

Most large services keep blocklists of temp-mail domains. The sign-up form says "code sent", the mail is silently
dropped, and your test or script fails for a reason that has nothing to do with your code. A brand-new catch-all
domain meets the same fate after a few hundred sign-ups.

OTPGmail inboxes are ordinary Gmail and iCloud mailboxes, so the sender treats them like any other user. You never
log into the mailbox: the code appears in the dashboard and in the API response.

| | Disposable mail | Own catch-all domain | **OTPGmail** |
|---|---|---|---|
| Accepted by large services | Rarely | For a while | Yes – real Gmail / iCloud |
| Readable over HTTP | Sometimes | You build it | Yes – 3 endpoints |
| Parallel runs | Shared inbox | Yes | One inbox per order |
| Cost | Free | Domain + server | From 250 VND (≈ $0.01) per inbox, refunded if no code |

## How it works

```
POST /v1/orders {service, domain}  ──►  { orderId, email: "xxx@icloud.com", status: "waiting_code" }
        (use that address in the sign-up form you are automating/testing)
GET  /v1/orders/{orderId}  every 3–5 s  ──►  otp: [{ code: "123456", receivedAt }]   status: "completed"
POST /v1/orders/{orderId}/cancel        ──►  no code yet? full refund (automatic after 30 minutes anyway)
```

After the first code, further codes sent to the same address are free for 24 hours (useful for
"verify this new device" steps).

## Quick start

### 1. Get an API key

The dashboard is in Vietnamese; these are the only buttons you need:

1. Sign up at <https://otpgmail.net/register> (**Đăng ký** = sign up; email, username, password).
2. Top up: **Nạp tiền** → **USDT** (BEP20 / TRC20 / TON, from 2 USDT, credited automatically) or a Vietnamese bank QR.
3. Copy your key from **Tài khoản** (Account) → **API key**.

### 2. Run an example

```bash
export OTPGMAIL_API_KEY=og_xxx          # Windows: set OTPGMAIL_API_KEY=og_xxx

pip install requests && python python/otpgmail_client.py git icloud.com   # Python 3.8+
node node/otpgmail-client.mjs git icloud.com                               # Node.js 18+, no dependencies
php php/otpgmail_client.php git icloud.com                                 # PHP 7.4+ with ext-curl
cd csharp && dotnet run -- git icloud.com                                  # .NET 6+
./curl/quickstart.sh git icloud.com                                        # bash + curl + jq
```

`git` is the service code for GitHub – see [SERVICES.md](SERVICES.md) for all codes. Each example prints the rented
address, waits for the code (`OTPGMAIL_MAX_WAIT` seconds, default 600) and cancels the order for a refund if nothing arrives.

### 3. Or use it as a library

```python
from otpgmail_client import rent_with_fallback, wait_for_code

order = rent_with_fallback("git", prefer="icloud.com")   # iCloud is ~10% cheaper; falls back to Gmail
fill_signup_form(email=order["email"])                   # your code
code = wait_for_code(order["orderId"])                   # None => cancelled and refunded
```

```javascript
import { rentWithFallback, waitForCode } from './node/otpgmail-client.mjs';

const order = await rentWithFallback('git', 'icloud.com');
await fillSignupForm({ email: order.email });            // your code
const code = await waitForCode(order.orderId);           // null => cancelled and refunded
```

## API reference (short)

Base URL `https://otpgmail.net` · Auth `Authorization: Bearer <api_key>` (or `?api_key=`) · JSON in, JSON out.
Success: `{ "success": true, "data": ... }` · Failure: `{ "success": false, "error": { "code", "message" } }`.

| Method & path | Body | `data` |
|---|---|---|
| `GET /v1/balance` | – | `{ balance }` (VND) |
| `GET /v1/services` | – | `[{ code, name, price, stock, icloud: { price, stock } \| null }]` |
| `POST /v1/orders` | `{ service, domain?, quantity? }` | **array** of orders: `{ orderId, service, domain, email, status, price, otp, codeDeadlineAt, rentExpiresAt, createdAt }` |
| `GET /v1/orders/{orderId}` | – | one order; `otp` is `[{ code, receivedAt }]` |
| `POST /v1/orders/{orderId}/cancel` | – | the cancelled order (only while it has no code) |

- `domain`: `gmail.com` (default) or `icloud.com`. Existing integrations that never send it keep getting Gmail.
- `quantity`: 1–50 inboxes per request (default 1). You are charged only for inboxes actually issued.
- `status`: `waiting_code` → `completed` (first code arrived) or `cancelled` (by you, or automatically after 30 minutes with a refund).
- Timestamps are Unix seconds. Prices are in VND.

Full reference with every field: <https://otpgmail.net/app/docs?utm_source=github&utm_medium=readme&utm_campaign=en>

### Error codes worth handling

| Code | HTTP | Meaning | Charged? |
|---|---|---|---|
| `NO_MAILS_AVAILABLE`, `OUT_OF_STOCK` | 409/503 | No inbox in stock for that service + domain right now | No |
| `DOMAIN_UNAVAILABLE` | 409 | iCloud is not offered for this service (or is temporarily off) – retry with `gmail.com` | No |
| `INSUFFICIENT_BALANCE` | 402 | Top up first | No |
| `QUANTITY_EXCEEDED` | 400 | More than 50 inboxes in one request | No |
| `VALIDATION_ERROR` | 400 | Bad body, e.g. an unknown `domain` | No |
| `RATE_LIMITED` | 429 | Slow down and respect `Retry-After` | No |
| `WAITING_LIMIT_REACHED` | 429 | Too many of your orders are still waiting for a code – cancel some or let them finish | No |
| `UNAUTHORIZED` | 401 | Missing or wrong API key | No |

### Good practice

- **Send an `Idempotency-Key` header** when creating orders. CI retries happen; with the key a retry returns the original order instead of renting a second inbox.
- **Poll every 3–5 seconds.** Limits are 10 requests/second and 300/minute per key.
- **Cancel in your teardown** so failed runs are refunded immediately rather than after the 30-minute timeout.
- **Fall back between domains.** iCloud is about 10% cheaper but shares one stock pool across services; when it is empty, Gmail usually is not.
- **Set a User-Agent.** Python's built-in `urllib` sends `Python-urllib/3.x`, which the CDN rejects with HTTP 403. `requests`, `fetch`, `curl`, `HttpClient` and PHP cURL all work; the examples send `otpgmail-client/1.0`.
- **Size timeouts per sender.** Delivery time is decided by the service you sign up to, not by the inbox.

## Delivery statistics (production, last 30 days, 2026-09-19)

| Service | Code | Orders with a code | Median wait |
|---|---|---|---|
| GitHub | `git` | 78% | ~30 s |
| AWS | `aws` | 96% | ~40 s |
| Shopee | `ka` | 95% | ~45 s |
| Microsoft | `mm` | 89% | ~2 min |
| TikTok | `lf` | 88% | ~50 s |
| OpenAI / ChatGPT | `dr` | 70% | ~4 min |
| Facebook | `fb` | 63% | ~1 min |

Misses are mostly the *sender* asking for an extra check (captcha, phone number) before it sends anything, which is
why every order without a code is refunded automatically. More than 70,000 inboxes have been issued since July 2026.

## Pricing

- From **250 VND (≈ $0.01)** per inbox; most popular services cost 350–900 VND (≈ $0.013–0.035). iCloud is about 10% cheaper than Gmail.
- Charged only when an inbox is issued. No code within 30 minutes → automatic 100% refund to your balance.
- Top up with **USDT** (BEP20 / TRC20 / TON) from 2 USDT, or Vietnamese bank transfer. No deposit fee.
- Live prices and stock: <https://otpgmail.net/app/services?utm_source=github&utm_medium=readme&utm_campaign=en>

## FAQ

**Is this a temp-mail service?** No. Inboxes are real Gmail / iCloud mailboxes rented per order; that is the whole point.

**Do I get the mailbox password?** No. You get the address and the codes. You cannot send mail or read unrelated messages.

**How long does an order live?** Up to 30 minutes waiting for the first code; after it arrives, further codes are delivered free for 24 hours.

**The service I need is not listed.** Use the code `ot` ("any service") or request it from the rent page (**Thêm dịch vụ**); new services are added when the infrastructure supports them.

**Can I buy in bulk?** Yes: `quantity` up to 50 per API request, or batch purchase (up to 200 inboxes, 24-hour hold) in the dashboard.

**Gmail or iCloud?** Same behaviour and refund policy. iCloud is cheaper; Gmail has the deeper stock. Apple ID (`wx`) is Gmail-only.

## Responsible use

Use rented inboxes in line with the terms of service of the platforms you register on – typical legitimate uses are
end-to-end tests of sign-up and email-verification flows, test accounts, and keeping project inboxes separate from
your personal address. OTPGmail does not support unlawful use.

## License

MIT – see [LICENSE](LICENSE). Issues and pull requests with clients for other languages are welcome.
