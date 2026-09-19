// OTPGmail API client — Node.js 18+ (no dependencies).
// Rent a real Gmail/iCloud inbox, wait for the email verification code (OTP),
// cancel and get refunded if nothing arrives.  Docs: https://otpgmail.net/app/docs
//
//   OTPGMAIL_API_KEY=og_xxx node otpgmail-client.mjs git icloud.com
import { randomUUID } from 'node:crypto';

const BASE = process.env.OTPGMAIL_BASE ?? 'https://otpgmail.net';
const KEY = process.env.OTPGMAIL_API_KEY;
const MAX_WAIT_MS = Number(process.env.OTPGMAIL_MAX_WAIT ?? 600) * 1000;
if (!KEY) throw new Error('Set OTPGMAIL_API_KEY');

// Nothing is charged for these: try the other domain, another service, or retry later.
const SOFT_ERRORS = new Set(['NO_MAILS_AVAILABLE', 'OUT_OF_STOCK', 'DOMAIN_UNAVAILABLE']);

export class ApiError extends Error {
  constructor(code, message) {
    super(`${code}: ${message}`);
    this.code = code;
  }
}

async function call(path, init = {}) {
  const res = await fetch(BASE + path, {
    ...init,
    headers: {
      Authorization: `Bearer ${KEY}`,
      'Content-Type': 'application/json',
      'User-Agent': 'otpgmail-client/1.0 (+https://otpgmail.net)',
      ...(init.headers ?? {}),
    },
  });
  const body = await res.json();
  if (!body.success) throw new ApiError(body.error?.code ?? 'UNKNOWN', body.error?.message ?? '');
  return body.data;
}

/** Account balance in VND. */
export const balance = async () => (await call('/v1/balance')).balance;

/** [{ code, name, price, stock, icloud: { price, stock } | null }] — prices in VND. */
export const services = () => call('/v1/services');

/** Rent one inbox → { orderId, email, domain, status, price, otp, codeDeadlineAt, ... } */
export async function rent(service, domain = 'gmail.com') {
  const data = await call('/v1/orders', {
    method: 'POST',
    body: JSON.stringify({ service, domain }),
    // Optional but recommended: a retried request returns the original order instead of renting twice.
    headers: { 'Idempotency-Key': randomUUID() },
  });
  return data[0]; // `data` is an array, even for quantity = 1
}

export const getOrder = (id) => call(`/v1/orders/${id}`);

/** Cancel an order that has no code yet → full refund. */
export const cancel = (id) => call(`/v1/orders/${id}/cancel`, { method: 'POST' });

/** Poll until a code arrives. Resolves to the code, or null (order cancelled and refunded). */
export async function waitForCode(id, { maxWaitMs = MAX_WAIT_MS, intervalMs = 4000 } = {}) {
  const deadline = Date.now() + maxWaitMs;
  while (Date.now() < deadline) {
    const order = await getOrder(id);
    if (order.otp.length) return order.otp.at(-1).code;
    if (order.status === 'cancelled' || order.status === 'failed') return null;
    await new Promise((r) => setTimeout(r, intervalMs)); // 3–5 s keeps you far below the rate limit
  }
  await cancel(id); // refund now instead of waiting for the 30-minute auto-refund
  return null;
}

/** Prefer the cheaper iCloud inbox; fall back to Gmail when it is unavailable. */
export async function rentWithFallback(service, prefer = 'icloud.com') {
  try {
    return await rent(service, prefer);
  } catch (e) {
    if (e instanceof ApiError && SOFT_ERRORS.has(e.code)) {
      return rent(service, prefer === 'icloud.com' ? 'gmail.com' : 'icloud.com');
    }
    throw e;
  }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [service = 'git', domain = 'gmail.com'] = process.argv.slice(2);
  console.log('Balance (VND):', await balance());
  const order = await rent(service, domain);
  console.log('Use this address in the sign-up form:', order.email);
  const code = await waitForCode(order.orderId);
  console.log('Code:', code ?? 'none - order cancelled and refunded');
}
