#!/usr/bin/env bash
# OTPGmail API with plain curl + jq.   Docs: https://otpgmail.net/app/docs
#   OTPGMAIL_API_KEY=og_xxx ./quickstart.sh git icloud.com
set -euo pipefail
BASE="${OTPGMAIL_BASE:-https://otpgmail.net}"
AUTH="Authorization: Bearer ${OTPGMAIL_API_KEY:?Set OTPGMAIL_API_KEY}"
SERVICE="${1:-git}"; DOMAIN="${2:-gmail.com}"; MAX_WAIT="${OTPGMAIL_MAX_WAIT:-600}"

echo "Balance (VND): $(curl -fsS -H "$AUTH" "$BASE/v1/balance" | jq -r .data.balance)"

# 1) Rent an inbox (Idempotency-Key: a retried request never rents twice)
ORDER=$(curl -sS -X POST "$BASE/v1/orders" -H "$AUTH" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)" \
  -d "{\"service\":\"$SERVICE\",\"domain\":\"$DOMAIN\"}")
[ "$(echo "$ORDER" | jq -r .success)" = "true" ] || { echo "Error: $(echo "$ORDER" | jq -c .error)"; exit 1; }
ID=$(echo "$ORDER" | jq -r '.data[0].orderId')
echo "Use this address in the sign-up form: $(echo "$ORDER" | jq -r '.data[0].email')"

# 2) Poll every 4 s until the code arrives
END=$((SECONDS + MAX_WAIT))
while [ $SECONDS -lt $END ]; do
  O=$(curl -fsS -H "$AUTH" "$BASE/v1/orders/$ID")
  CODE=$(echo "$O" | jq -r '.data.otp[-1].code // empty')
  [ -n "$CODE" ] && { echo "Code: $CODE"; exit 0; }
  [ "$(echo "$O" | jq -r .data.status)" = "cancelled" ] && { echo "Order was cancelled and refunded"; exit 2; }
  sleep 4
done

# 3) Nothing arrived: cancel now → full refund
curl -fsS -X POST -H "$AUTH" "$BASE/v1/orders/$ID/cancel" | jq -r '"Cancelled: \(.data.status) - refunded"'
