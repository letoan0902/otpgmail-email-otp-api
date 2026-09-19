// OTPGmail API client — C# / .NET 6+ (no NuGet packages).
// Rent a real Gmail/iCloud inbox, wait for the email verification code (OTP),
// cancel and get refunded if nothing arrives.  Docs: https://otpgmail.net/app/docs
//
//   set OTPGMAIL_API_KEY=og_xxx        (Linux/macOS: export OTPGMAIL_API_KEY=og_xxx)
//   dotnet run -- git icloud.com
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var service = args.Length > 0 ? args[0] : "git";
var domain = args.Length > 1 ? args[1] : "gmail.com";

var client = new OtpGmailClient(
    Environment.GetEnvironmentVariable("OTPGMAIL_API_KEY") ?? throw new InvalidOperationException("Set OTPGMAIL_API_KEY"),
    Environment.GetEnvironmentVariable("OTPGMAIL_BASE") ?? "https://otpgmail.net");
var maxWait = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("OTPGMAIL_MAX_WAIT"), out var s) ? s : 600);

Console.WriteLine($"Balance (VND): {await client.BalanceAsync()}");
var order = await client.RentAsync(service, domain);
Console.WriteLine($"Use this address in the sign-up form: {order.GetProperty("email").GetString()}");
var code = await client.WaitForCodeAsync(order.GetProperty("orderId").GetString()!, maxWait);
Console.WriteLine($"Code: {code ?? "none - order cancelled and refunded"}");

public sealed class OtpGmailException : Exception
{
    public string Code { get; }
    public OtpGmailException(string code, string message) : base($"{code}: {message}") => Code = code;
}

public sealed class OtpGmailClient
{
    // Nothing is charged for these: try the other domain, another service, or retry later.
    private static readonly HashSet<string> SoftErrors = new() { "NO_MAILS_AVAILABLE", "OUT_OF_STOCK", "DOMAIN_UNAVAILABLE" };
    private readonly HttpClient _http;

    public OtpGmailClient(string apiKey, string baseUrl = "https://otpgmail.net")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("otpgmail-client/1.0");
    }

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, object? body = null, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body);
        if (idempotencyKey != null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        using var res = await _http.SendAsync(req);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
        {
            var code = "UNKNOWN";
            var message = "";
            if (root.TryGetProperty("error", out var err))
            {
                code = err.TryGetProperty("code", out var c) ? c.GetString() ?? code : code;
                message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            }
            throw new OtpGmailException(code, message);
        }
        return root.GetProperty("data").Clone();
    }

    /// <summary>Account balance in VND.</summary>
    public async Task<long> BalanceAsync() => (await CallAsync(HttpMethod.Get, "/v1/balance")).GetProperty("balance").GetInt64();

    /// <summary>[{code, name, price, stock, icloud: {price, stock} | null}] — prices in VND.</summary>
    public Task<JsonElement> ServicesAsync() => CallAsync(HttpMethod.Get, "/v1/services");

    /// <summary>Rent one inbox → {orderId, email, domain, status, price, otp, codeDeadlineAt, ...}</summary>
    public async Task<JsonElement> RentAsync(string service, string domain = "gmail.com")
    {
        // Idempotency-Key is optional but recommended: a retried request returns the
        // original order instead of renting (and paying for) a second inbox.
        var data = await CallAsync(HttpMethod.Post, "/v1/orders", new { service, domain }, Guid.NewGuid().ToString());
        return data[0]; // `data` is an array, even for quantity = 1
    }

    public Task<JsonElement> GetOrderAsync(string orderId) => CallAsync(HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(orderId)}");

    /// <summary>Cancel an order that has no code yet → full refund.</summary>
    public Task<JsonElement> CancelAsync(string orderId) => CallAsync(HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(orderId)}/cancel");

    /// <summary>Poll until a code arrives. Returns the code, or null (order cancelled and refunded).</summary>
    public async Task<string?> WaitForCodeAsync(string orderId, TimeSpan maxWait, int intervalSeconds = 4)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadline)
        {
            var order = await GetOrderAsync(orderId);
            var otp = order.GetProperty("otp");
            if (otp.GetArrayLength() > 0) return otp[otp.GetArrayLength() - 1].GetProperty("code").GetString();
            var status = order.GetProperty("status").GetString();
            if (status is "cancelled" or "failed") return null; // the 30-minute server-side timeout already refunded it
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds)); // 3–5 s keeps you far below the rate limit
        }
        await CancelAsync(orderId); // refund now instead of waiting for the auto-refund
        return null;
    }

    /// <summary>Prefer the cheaper iCloud inbox; fall back to Gmail when it is unavailable.</summary>
    public async Task<JsonElement> RentWithFallbackAsync(string service, string prefer = "icloud.com")
    {
        try
        {
            return await RentAsync(service, prefer);
        }
        catch (OtpGmailException e) when (SoftErrors.Contains(e.Code))
        {
            return await RentAsync(service, prefer == "icloud.com" ? "gmail.com" : "icloud.com");
        }
    }
}
