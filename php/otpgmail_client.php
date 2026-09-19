<?php
/**
 * OTPGmail API client — PHP 7.4+ (ext-curl, ext-json).
 * Rent a real Gmail/iCloud inbox, wait for the email verification code (OTP),
 * cancel and get refunded if nothing arrives.  Docs: https://otpgmail.net/app/docs
 *
 *   OTPGMAIL_API_KEY=og_xxx php otpgmail_client.php git icloud.com
 */

// Nothing is charged for these: try the other domain, another service, or retry later.
const OTPGMAIL_SOFT_ERRORS = ['NO_MAILS_AVAILABLE', 'OUT_OF_STOCK', 'DOMAIN_UNAVAILABLE'];

class OtpGmailError extends RuntimeException
{
    /** @var string */
    public $errorCode;

    public function __construct(string $code, string $message)
    {
        parent::__construct("$code: $message");
        $this->errorCode = $code;
    }
}

function otpgmail_call(string $method, string $path, ?array $body = null, array $extraHeaders = [])
{
    $base = getenv('OTPGMAIL_BASE') ?: 'https://otpgmail.net';
    $key = getenv('OTPGMAIL_API_KEY');
    if (!$key) {
        throw new RuntimeException('Set OTPGMAIL_API_KEY');
    }
    $ch = curl_init($base . $path);
    curl_setopt_array($ch, [
        CURLOPT_CUSTOMREQUEST => $method,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_TIMEOUT => 30,
        CURLOPT_HTTPHEADER => array_merge([
            'Authorization: Bearer ' . $key,
            'Content-Type: application/json',
            'User-Agent: otpgmail-client/1.0 (+https://otpgmail.net)',
        ], $extraHeaders),
    ]);
    if ($body !== null) {
        curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($body));
    }
    $raw = curl_exec($ch);
    if ($raw === false) {
        throw new RuntimeException('HTTP error: ' . curl_error($ch));
    }
    $json = json_decode($raw, true);
    if (!is_array($json) || empty($json['success'])) {
        $err = is_array($json) && isset($json['error']) ? $json['error'] : [];
        throw new OtpGmailError($err['code'] ?? 'UNKNOWN', $err['message'] ?? substr((string) $raw, 0, 200));
    }
    return $json['data'];
}

/** Account balance in VND. */
function otpgmail_balance(): int
{
    return (int) otpgmail_call('GET', '/v1/balance')['balance'];
}

/** [{code, name, price, stock, icloud: {price, stock} | null}] — prices in VND. */
function otpgmail_services(): array
{
    return otpgmail_call('GET', '/v1/services');
}

/** Rent one inbox → [orderId, email, domain, status, price, otp, codeDeadlineAt, ...] */
function otpgmail_rent(string $service, string $domain = 'gmail.com'): array
{
    // Idempotency-Key is optional but recommended: a retried request returns the
    // original order instead of renting (and paying for) a second inbox.
    $data = otpgmail_call('POST', '/v1/orders', ['service' => $service, 'domain' => $domain], [
        'Idempotency-Key: ' . bin2hex(random_bytes(16)),
    ]);
    return $data[0]; // `data` is a list, even for quantity = 1
}

function otpgmail_get_order(string $orderId): array
{
    return otpgmail_call('GET', '/v1/orders/' . rawurlencode($orderId));
}

/** Cancel an order that has no code yet → full refund. */
function otpgmail_cancel(string $orderId): array
{
    return otpgmail_call('POST', '/v1/orders/' . rawurlencode($orderId) . '/cancel');
}

/** Poll until a code arrives. Returns the code, or null (order cancelled and refunded). */
function otpgmail_wait_for_code(string $orderId, ?int $maxWait = null, int $interval = 4): ?string
{
    $maxWait = $maxWait ?? (int) (getenv('OTPGMAIL_MAX_WAIT') ?: 600);
    $deadline = time() + $maxWait;
    while (time() < $deadline) {
        $order = otpgmail_get_order($orderId);
        if (!empty($order['otp'])) {
            return end($order['otp'])['code'];
        }
        if (in_array($order['status'], ['cancelled', 'failed'], true)) {
            return null; // the 30-minute server-side timeout already refunded it
        }
        sleep($interval); // 3–5 s keeps you far below the rate limit
    }
    otpgmail_cancel($orderId); // refund now instead of waiting for the auto-refund
    return null;
}

/** Prefer the cheaper iCloud inbox; fall back to Gmail when it is unavailable. */
function otpgmail_rent_with_fallback(string $service, string $prefer = 'icloud.com'): array
{
    try {
        return otpgmail_rent($service, $prefer);
    } catch (OtpGmailError $e) {
        if (in_array($e->errorCode, OTPGMAIL_SOFT_ERRORS, true)) {
            return otpgmail_rent($service, $prefer === 'icloud.com' ? 'gmail.com' : 'icloud.com');
        }
        throw $e;
    }
}

if (PHP_SAPI === 'cli' && realpath($argv[0] ?? '') === __FILE__) {
    $service = $argv[1] ?? 'git';
    $domain = $argv[2] ?? 'gmail.com';
    echo 'Balance (VND): ', otpgmail_balance(), PHP_EOL;
    $order = otpgmail_rent($service, $domain);
    echo 'Use this address in the sign-up form: ', $order['email'], PHP_EOL;
    $code = otpgmail_wait_for_code($order['orderId']);
    echo 'Code: ', $code ?? 'none - order cancelled and refunded', PHP_EOL;
}
