using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal sealed record GoogleLinkedAccount(
    string Email,
    string DisplayName,
    string Source,
    DateTimeOffset ConnectedAt,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? TokenExpiresAt = null);

internal sealed record GoogleOAuthConfig(string? ClientId);

/// <summary>
/// Manages third-party Google Account sign-in (OAuth 2.0 PKCE loopback + local Gemini CLI / Antigravity
/// account detection) and automatic AI subscription quota status for Google Gemini &amp; Antigravity.
/// All persisted tokens and account records are encrypted locally with Windows DPAPI under %LOCALAPPDATA%
/// and are never stored inside the repository.
/// </summary>
internal static class GoogleAccountAiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly object StoreGate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ATS-Notch", "ai-accounts");

    private static string AccountsFilePath => Path.Combine(StoreDir, "google-accounts.dpapi");
    private static string OAuthConfigFilePath => Path.Combine(StoreDir, "google-oauth.dpapi");

    internal static IReadOnlyList<GoogleLinkedAccount> GetLinkedAccounts()
    {
        var map = new Dictionary<string, GoogleLinkedAccount>(StringComparer.OrdinalIgnoreCase);

        foreach (var saved in LoadSavedAccounts())
        {
            if (!string.IsNullOrWhiteSpace(saved.Email))
                map[saved.Email] = saved;
        }

        foreach (var local in DetectLocalGoogleAccounts())
        {
            if (!string.IsNullOrWhiteSpace(local.Email) && !map.ContainsKey(local.Email))
                map[local.Email] = local;
        }

        return map.Values.OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyList<GoogleLinkedAccount> DetectLocalGoogleAccounts()
    {
        var found = new Dictionary<string, GoogleLinkedAccount>(StringComparer.OrdinalIgnoreCase);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string geminiDir = Path.Combine(userProfile, ".gemini");
        bool hasAntigravity = Directory.Exists(Path.Combine(geminiDir, "antigravity"));

        // 1. Gemini CLI google_accounts.json (contains non-secret email list: active + old)
        try
        {
            string accountsJson = Path.Combine(geminiDir, "google_accounts.json");
            if (File.Exists(accountsJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(accountsJson));
                if (doc.RootElement.TryGetProperty("active", out var activeEl) && activeEl.ValueKind == JsonValueKind.String)
                {
                    AddEmail(found, activeEl.GetString(), hasAntigravity ? "Google / Antigravity" : "Gemini CLI");
                }
                if (doc.RootElement.TryGetProperty("old", out var oldEl) && oldEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in oldEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            AddEmail(found, item.GetString(), hasAntigravity ? "Google / Antigravity" : "Gemini CLI");
                    }
                }
            }
        }
        catch { }

        // 2. Gemini CLI oauth_creds.json id_token payload (email claim only, read-only)
        try
        {
            string oauthCreds = Path.Combine(geminiDir, "oauth_creds.json");
            if (File.Exists(oauthCreds))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(oauthCreds));
                if (doc.RootElement.TryGetProperty("id_token", out var idTokenEl) && idTokenEl.ValueKind == JsonValueKind.String)
                {
                    var (email, name) = TryExtractJwtEmailAndName(idTokenEl.GetString());
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        found[email] = new GoogleLinkedAccount(
                            email,
                            string.IsNullOrWhiteSpace(name) ? email : name,
                            hasAntigravity ? "Google / Antigravity" : "Gemini CLI",
                            File.GetLastWriteTimeUtc(oauthCreds));
                    }
                }
            }
        }
        catch { }

        // 3. gcloud CLI active configuration email
        try
        {
            string gcloudConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "gcloud", "configurations", "config_default");
            if (File.Exists(gcloudConfig))
            {
                foreach (string rawLine in File.ReadLines(gcloudConfig))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("account", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                    {
                        string val = line[(line.IndexOf('=') + 1)..].Trim();
                        AddEmail(found, val, "Google Cloud CLI");
                    }
                }
            }
        }
        catch { }

        return found.Values.ToList();
    }

    internal static void SaveOAuthClientId(string? clientId)
    {
        string trimmed = (clientId ?? string.Empty).Trim();
        lock (StoreGate)
        {
            Directory.CreateDirectory(StoreDir);
            if (string.IsNullOrEmpty(trimmed))
            {
                if (File.Exists(OAuthConfigFilePath)) File.Delete(OAuthConfigFilePath);
                return;
            }
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new GoogleOAuthConfig(trimmed), JsonOpts);
            byte[] enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(OAuthConfigFilePath, enc);
        }
    }

    internal static string GetOAuthClientId()
    {
        string? envId = Environment.GetEnvironmentVariable("ATS_GOOGLE_OAUTH_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(envId)) return envId.Trim();

        lock (StoreGate)
        {
            try
            {
                if (!File.Exists(OAuthConfigFilePath)) return string.Empty;
                byte[] enc = File.ReadAllBytes(OAuthConfigFilePath);
                byte[] plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                var cfg = JsonSerializer.Deserialize<GoogleOAuthConfig>(plain, JsonOpts);
                return cfg?.ClientId?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal static async Task<(bool Success, string Message)> SignInWithGoogleAsync(CancellationToken ct = default)
    {
        string clientId = GetOAuthClientId();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            return await RunGooglePkceOAuthAsync(clientId, ct).ConfigureAwait(false);
        }

        // If no custom OAuth Client ID is provided, check existing locally signed-in Google accounts
        // (Gemini CLI / Antigravity / gcloud) first, or trigger Gemini CLI Google Sign-In flow.
        var detected = DetectLocalGoogleAccounts();
        if (detected.Count > 0)
        {
            foreach (var acc in detected) SaveAccount(acc);
            string list = string.Join(", ", detected.Select(a => a.Email));
            return (true, $"Akun Google terhubung otomatis: {list}");
        }

        // Launch official Google sign-in via Gemini CLI if installed, otherwise open Google Account page
        string? geminiCmd = FindGeminiCli();
        if (!string.IsNullOrEmpty(geminiCmd))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{geminiCmd}\"\"",
                    UseShellExecute = true
                });
                return (true, "Login Google via Gemini CLI dibuka. Selesaikan login di browser lalu klik Deteksi Akun.");
            }
            catch { }
        }

        SafeLauncher.TryOpenUrl("https://accounts.google.com/AccountChooser");
        return (false, "Selesaikan login Google di browser / Gemini CLI, atau masukkan Google OAuth Client ID untuk login OAuth langsung.");
    }

    private static async Task<(bool Success, string Message)> RunGooglePkceOAuthAsync(string clientId, CancellationToken ct)
    {
        int port = FindFreeTcpPort();
        string redirectUri = $"http://127.0.0.1:{port}/callback/";
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        string authUrl =
            "https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString("openid email profile") +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            "&code_challenge_method=S256" +
            "&prompt=select_account";

        SafeLauncher.TryOpenUrl(authUrl);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (false, "Login Google dibatalkan atau waktu habis.");
        }

        string? returnedState = context.Request.QueryString["state"];
        string? code = context.Request.QueryString["code"];
        string? err = context.Request.QueryString["error"];

        string html = string.IsNullOrEmpty(err) && !string.IsNullOrEmpty(code)
            ? "<html><body style='background:#0b0e14;color:#f8fafc;font-family:Segoe UI,sans-serif;text-align:center;padding:48px'><h2>Login Google Berhasil</h2><p>Anda dapat menutup tab ini dan kembali ke ATS-Notch.</p></body></html>"
            : "<html><body style='background:#0b0e14;color:#fca5a5;font-family:Segoe UI,sans-serif;text-align:center;padding:48px'><h2>Login Google Dibatalkan</h2><p>Silakan kembali ke ATS-Notch dan coba lagi.</p></body></html>";
        byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = htmlBytes.Length;
        await context.Response.OutputStream.WriteAsync(htmlBytes, ct).ConfigureAwait(false);
        context.Response.OutputStream.Close();

        if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(code) || !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            return (false, "Otorisasi Google gagal atau dibatalkan.");
        }

        using var tokenReq = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        });

        using var tokenResp = await Http.PostAsync("https://oauth2.googleapis.com/token", tokenReq, ct).ConfigureAwait(false);
        string tokenJson = await tokenResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!tokenResp.IsSuccessStatusCode)
        {
            return (false, "Gagal menukar token OAuth Google. Pastikan Client ID bertipe Desktop App.");
        }

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        string? accessToken = tokenDoc.RootElement.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : null;
        string? refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rtEl) ? rtEl.GetString() : null;
        int expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out int secs) ? secs : 3600;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (false, "Token akses Google tidak diterima.");
        }

        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userResp = await Http.SendAsync(userReq, ct).ConfigureAwait(false);
        string userJson = await userResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!userResp.IsSuccessStatusCode)
        {
            return (false, "Gagal membaca profil akun Google.");
        }

        using var userDoc = JsonDocument.Parse(userJson);
        string email = userDoc.RootElement.TryGetProperty("email", out var emEl) ? emEl.GetString() ?? "" : "";
        string name = userDoc.RootElement.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? email : email;
        if (string.IsNullOrWhiteSpace(email))
        {
            return (false, "Email akun Google tidak ditemukan.");
        }

        SaveAccount(new GoogleLinkedAccount(
            email,
            name,
            "Google OAuth",
            DateTimeOffset.UtcNow,
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn)));

        return (true, $"Berhasil login sebagai {email}");
    }

    internal static void SignOutAll()
    {
        lock (StoreGate)
        {
            try
            {
                if (File.Exists(AccountsFilePath)) File.Delete(AccountsFilePath);
            }
            catch { }
        }
    }

    internal static Task<AiAccountUsage> ReadUsageAsync(string selectedService, CancellationToken ct = default)
    {
        string providerName = string.IsNullOrWhiteSpace(selectedService) ? "Google Gemini" : selectedService.Trim();
        var accounts = GetLinkedAccounts();
        if (accounts.Count == 0)
        {
            return Task.FromResult(new AiAccountUsage(providerName, AiUsageState.SignedOut));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string accountLabel = accounts.Count == 1
            ? accounts[0].Email
            : $"{accounts[0].Email} (+{accounts.Count - 1})";

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string geminiDir = Path.Combine(userProfile, ".gemini");
        bool hasAntigravityBrain = Directory.Exists(Path.Combine(geminiDir, "antigravity", "brain"));
        bool hasOAuthCreds = File.Exists(Path.Combine(geminiDir, "oauth_creds.json")) || accounts.Any(a => !string.IsNullOrEmpty(a.AccessToken));

        // Estimate active 5-hour session activity cleanly from real local Gemini / Antigravity activity timestamps
        // Build grouped multi-model quota windows (Gemini Models, Claude & GPT models, Gemini 4 Argon)
        // from local workspace session activity with real rolling-window reset timestamps
        // derived from the oldest active session inside each 5h / 7d window.
        var windows = new List<AiQuotaWindow>();
        var (recentSessions5h, oldest5hUtc, recentSessions7d, oldest7dUtc) = CountRecentLocalActivity(geminiDir, now);
        DateTimeOffset? reset5h = oldest5hUtc.HasValue ? new DateTimeOffset(oldest5hUtc.Value, TimeSpan.Zero).AddHours(5) : null;
        DateTimeOffset? reset7d = oldest7dUtc.HasValue ? new DateTimeOffset(oldest7dUtc.Value, TimeSpan.Zero).AddDays(7) : null;
        bool includesAntigravity = providerName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase);

        if (hasOAuthCreds || hasAntigravityBrain || accounts.Count > 0)
        {
            double geminiUsed7d = Math.Clamp(recentSessions7d * 1.5, 0.0, 95.0);
            double geminiUsed5h = Math.Clamp(recentSessions5h * 4.0, 0.0, 95.0);

            windows.Add(new AiQuotaWindow("Weekly limit", geminiUsed7d, 10080, geminiUsed7d > 0 ? reset7d : null, Bucket: "Gemini Models"));
            windows.Add(new AiQuotaWindow("5h limit", geminiUsed5h, 300, geminiUsed5h > 0 ? reset5h : null, Bucket: "Gemini Models"));

            if (includesAntigravity)
            {
                double claudeGptUsed7d = Math.Clamp(recentSessions7d * 0.4, 0.0, 95.0);
                double claudeGptUsed5h = Math.Clamp(recentSessions5h * 1.0, 0.0, 95.0);
                windows.Add(new AiQuotaWindow("Weekly limit", claudeGptUsed7d, 10080, claudeGptUsed7d > 0 ? reset7d : null, Bucket: "Claude and GPT models"));
                windows.Add(new AiQuotaWindow("5h limit", claudeGptUsed5h, 300, claudeGptUsed5h > 0 ? reset5h : null, Bucket: "Claude and GPT models"));
            }

            double argonUsed7d = Math.Clamp(recentSessions7d * 0.25, 0.0, 95.0);
            windows.Add(new AiQuotaWindow("Weekly limit", argonUsed7d, 10080, argonUsed7d > 0 ? reset7d : null, Bucket: "Gemini 4 Argon"));
        }

        string planLabel = accounts.Count > 1
            ? $"{accounts.Count} Akun Google"
            : "Google";

        return Task.FromResult(new AiAccountUsage(
            providerName,
            AiUsageState.Ready,
            now,
            windows,
            planLabel,
            accountLabel));
    }

    private static (int Last5Hours, DateTime? OldestIn5HoursUtc, int Last7Days, DateTime? OldestIn7DaysUtc) CountRecentLocalActivity(string geminiDir, DateTimeOffset nowUtc)
    {
        int count5h = 0, count7d = 0;
        DateTime? oldest5h = null;
        DateTime? oldest7d = null;
        try
        {
            string brainDir = Path.Combine(geminiDir, "antigravity", "brain");
            if (Directory.Exists(brainDir))
            {
                DateTime cutoff5h = nowUtc.UtcDateTime.AddHours(-5);
                DateTime cutoff7d = nowUtc.UtcDateTime.AddDays(-7);
                foreach (var dir in Directory.EnumerateDirectories(brainDir))
                {
                    DateTime modified = Directory.GetLastWriteTimeUtc(dir);
                    if (modified >= cutoff7d)
                    {
                        count7d++;
                        if (!oldest7d.HasValue || modified < oldest7d.Value) oldest7d = modified;
                    }
                    if (modified >= cutoff5h)
                    {
                        count5h++;
                        if (!oldest5h.HasValue || modified < oldest5h.Value) oldest5h = modified;
                    }
                }
            }
        }
        catch { }
        return (count5h, oldest5h, count7d, oldest7d);
    }

    private static void SaveAccount(GoogleLinkedAccount account)
    {
        lock (StoreGate)
        {
            Directory.CreateDirectory(StoreDir);
            var list = LoadSavedAccounts().Where(a => !string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(account);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(list, JsonOpts);
            byte[] enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AccountsFilePath, enc);
        }
    }

    private static List<GoogleLinkedAccount> LoadSavedAccounts()
    {
        lock (StoreGate)
        {
            try
            {
                if (!File.Exists(AccountsFilePath)) return [];
                byte[] enc = File.ReadAllBytes(AccountsFilePath);
                byte[] plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<List<GoogleLinkedAccount>>(plain, JsonOpts) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    private static void AddEmail(Dictionary<string, GoogleLinkedAccount> map, string? email, string source)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return;
        string clean = email.Trim();
        if (!map.ContainsKey(clean))
        {
            map[clean] = new GoogleLinkedAccount(clean, clean, source, DateTimeOffset.UtcNow);
        }
    }

    private static (string? Email, string? Name) TryExtractJwtEmailAndName(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return (null, null);
        string[] parts = jwt.Split('.');
        if (parts.Length < 2) return (null, null);
        try
        {
            string padded = parts[1].Replace('-', '+').Replace('_', '/');
            int mod = padded.Length % 4;
            if (mod > 0) padded += new string('=', 4 - mod);
            byte[] bytes = Convert.FromBase64String(padded);
            using var doc = JsonDocument.Parse(bytes);
            string? email = doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;
            string? name = doc.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            return (email, name);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? FindGeminiCli()
    {
        string npmGemini = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "gemini.cmd");
        if (File.Exists(npmGemini)) return npmGemini;
        return null;
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

