"""OTPGmail API client — Python 3.8+ (pip install requests).

Rent a real Gmail/iCloud inbox, wait for the email verification code (OTP),
cancel and get refunded if nothing arrives.  Docs: https://otpgmail.net/app/docs

    export OTPGMAIL_API_KEY=og_xxx
    python otpgmail_client.py git              # GitHub, @gmail.com inbox
    python otpgmail_client.py git icloud.com   # GitHub, @icloud.com inbox (~10% cheaper)

Note: use `requests` (or set your own User-Agent). Python's built-in urllib sends
"Python-urllib/3.x", which our CDN rejects with HTTP 403.
"""
import os
import sys
import time
import uuid

import requests

BASE = os.environ.get("OTPGMAIL_BASE", "https://otpgmail.net")
MAX_WAIT = int(os.environ.get("OTPGMAIL_MAX_WAIT", "600"))  # seconds to wait for a code
HEADERS = {
    "Authorization": f"Bearer {os.environ['OTPGMAIL_API_KEY']}",
    "User-Agent": "otpgmail-client/1.0 (+https://otpgmail.net)",
}

# Nothing is charged for these: try the other domain, another service, or retry later.
SOFT_ERRORS = {"NO_MAILS_AVAILABLE", "OUT_OF_STOCK", "DOMAIN_UNAVAILABLE"}


class ApiError(RuntimeError):
    def __init__(self, code, message):
        super().__init__(f"{code}: {message}")
        self.code = code


def _data(resp):
    body = resp.json()
    if not body.get("success"):
        err = body.get("error") or {}
        raise ApiError(err.get("code", "UNKNOWN"), err.get("message", ""))
    return body["data"]


def balance():
    """Account balance in VND."""
    return _data(requests.get(f"{BASE}/v1/balance", headers=HEADERS, timeout=30))["balance"]


def services():
    """[{code, name, price, stock, icloud: {price, stock} | None}] — prices in VND."""
    return _data(requests.get(f"{BASE}/v1/services", headers=HEADERS, timeout=30))


def rent(service, domain="gmail.com"):
    """Rent one inbox. Returns {orderId, email, domain, status, price, otp, codeDeadlineAt, ...}."""
    resp = requests.post(
        f"{BASE}/v1/orders",
        json={"service": service, "domain": domain},
        # Optional but recommended: a retried request returns the original order
        # instead of renting (and paying for) a second inbox.
        headers={**HEADERS, "Idempotency-Key": str(uuid.uuid4())},
        timeout=30,
    )
    return _data(resp)[0]  # `data` is a list, even for quantity = 1


def get_order(order_id):
    return _data(requests.get(f"{BASE}/v1/orders/{order_id}", headers=HEADERS, timeout=30))


def cancel(order_id):
    """Cancel an order that has no code yet -> full refund."""
    return _data(requests.post(f"{BASE}/v1/orders/{order_id}/cancel", headers=HEADERS, timeout=30))


def wait_for_code(order_id, max_wait=MAX_WAIT, interval=4):
    """Poll until a code arrives. Returns the code, or None (order cancelled and refunded)."""
    deadline = time.time() + max_wait
    while time.time() < deadline:
        order = get_order(order_id)
        if order["otp"]:
            return order["otp"][-1]["code"]
        if order["status"] in ("cancelled", "failed"):
            return None  # the 30-minute server-side timeout already refunded it
        time.sleep(interval)  # 3-5 s keeps you far below the rate limit
    cancel(order_id)  # out of patience: refund now instead of waiting for the auto-refund
    return None


def rent_with_fallback(service, prefer="icloud.com"):
    """Prefer the cheaper iCloud inbox; fall back to Gmail when it is unavailable."""
    other = "gmail.com" if prefer == "icloud.com" else "icloud.com"
    try:
        return rent(service, prefer)
    except ApiError as e:
        if e.code in SOFT_ERRORS:
            return rent(service, other)
        raise


if __name__ == "__main__":
    svc = sys.argv[1] if len(sys.argv) > 1 else "git"
    dom = sys.argv[2] if len(sys.argv) > 2 else "gmail.com"
    print("Balance (VND):", balance())
    order = rent(svc, dom)
    print("Use this address in the sign-up form:", order["email"])
    code = wait_for_code(order["orderId"])
    print("Code:", code if code else "none - order cancelled and refunded")
