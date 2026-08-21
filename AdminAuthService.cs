#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

/// <summary>
/// 管理端密码与内存会话服务。
/// 密码只以 PBKDF2 哈希写入 config.json；会话令牌只通过 HttpOnly Cookie 传递。
/// </summary>
public sealed class AdminAuthService
{
    public const string SessionCookieName = "lsg_admin_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private const int PasswordHashIterations = 310_000;
    private const int PasswordSaltBytes = 16;
    private const int PasswordHashBytes = 32;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginAttemptState> _loginAttempts = new(StringComparer.Ordinal);

    public bool IsPasswordConfigured
    {
        get
        {
            lock (Globals.ConfigLock)
                return !string.IsNullOrWhiteSpace(Globals.Config.AdminPasswordHash);
        }
    }

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordHashIterations,
            HashAlgorithmName.SHA256,
            PasswordHashBytes);

        return $"pbkdf2-sha256${PasswordHashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static string? ValidateNewPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "密码不能为空";
        if (password.Length < 12)
            return "密码至少需要 12 个字符";
        if (password.Length > 256)
            return "密码不能超过 256 个字符";
        return null;
    }

    public bool TryValidateCredentials(
        string? password,
        string clientKey,
        out bool rateLimited,
        out TimeSpan retryAfter)
    {
        rateLimited = false;
        retryAfter = TimeSpan.Zero;

        var state = _loginAttempts.GetOrAdd(clientKey, _ => new LoginAttemptState());
        DateTime now = DateTime.UtcNow;

        lock (state.SyncRoot)
        {
            if (state.BlockedUntilUtc > now)
            {
                rateLimited = true;
                retryAfter = state.BlockedUntilUtc - now;
                return false;
            }

            state.Failures.RemoveAll(at => now - at > AttemptWindow);
        }

        bool valid = VerifyConfiguredPassword(password);
        lock (state.SyncRoot)
        {
            if (valid)
            {
                state.Failures.Clear();
                state.BlockedUntilUtc = DateTime.MinValue;
                _loginAttempts.TryRemove(clientKey, out _);
                return true;
            }

            state.Failures.Add(now);
            if (state.Failures.Count >= MaxFailedAttempts)
            {
                state.Failures.Clear();
                state.BlockedUntilUtc = now.Add(LockoutDuration);
                rateLimited = true;
                retryAfter = LockoutDuration;
            }
        }

        return false;
    }

    public bool VerifyConfiguredPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        string encoded;
        lock (Globals.ConfigLock)
            encoded = Globals.Config.AdminPasswordHash ?? "";

        try
        {
            string[] parts = encoded.Split('$');
            if (parts.Length != 4 || !parts[0].Equals("pbkdf2-sha256", StringComparison.Ordinal))
                return false;
            if (!int.TryParse(parts[1], out int iterations) || iterations < 100_000 || iterations > 2_000_000)
                return false;

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            if (salt.Length < 16 || expected.Length != PasswordHashBytes)
                return false;

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public string CreateSession(string? adminPassword = null)
    {
        CleanupExpiredSessions();
        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string playbackToken = "";
        if (!string.IsNullOrEmpty(adminPassword))
        {
            string encryptedPlaybackToken;
            string playbackTokenHash;
            lock (Globals.ConfigLock)
            {
                encryptedPlaybackToken = Globals.Config.PlaybackTokenEncrypted ?? "";
                playbackTokenHash = Globals.Config.PlaybackTokenHash ?? "";
            }

            if (PlaybackTokenProtector.TryUnprotect(encryptedPlaybackToken, adminPassword, out string recovered) &&
                PlaybackTokenProtector.ValidateToken(recovered, playbackTokenHash))
                playbackToken = recovered;
        }

        _sessions[GetSessionKey(token)] = new AdminSession(DateTime.UtcNow.Add(SessionLifetime), playbackToken);
        return token;
    }

    public bool IsAuthenticated(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(SessionCookieName, out string? token) || string.IsNullOrWhiteSpace(token))
            return false;

        string key = GetSessionKey(token);
        if (!_sessions.TryGetValue(key, out AdminSession? session))
            return false;
        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    public bool TryGetPlaybackToken(HttpRequest request, out string token)
    {
        token = "";
        if (!request.Cookies.TryGetValue(SessionCookieName, out string? sessionToken) || string.IsNullOrWhiteSpace(sessionToken))
            return false;
        if (!_sessions.TryGetValue(GetSessionKey(sessionToken), out AdminSession? session) ||
            session.ExpiresAtUtc <= DateTime.UtcNow ||
            string.IsNullOrWhiteSpace(session.PlaybackToken))
            return false;

        token = session.PlaybackToken;
        return true;
    }

    public void RevokeSession(HttpRequest request)
    {
        if (request.Cookies.TryGetValue(SessionCookieName, out string? token) && !string.IsNullOrWhiteSpace(token))
            _sessions.TryRemove(GetSessionKey(token), out _);
    }

    public void InvalidateAllSessions() => _sessions.Clear();

    public static void AppendSessionCookie(HttpResponse response, string token, bool isHttps)
    {
        response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            MaxAge = SessionLifetime,
            Expires = DateTimeOffset.UtcNow.Add(SessionLifetime)
        });
    }

    public static void DeleteSessionCookie(HttpResponse response, bool isHttps)
    {
        response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/"
        });
    }

    public static bool IsLoopbackRequest(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private void CleanupExpiredSessions()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAtUtc <= now)
                _sessions.TryRemove(item.Key, out _);
        }
    }

    private static string GetSessionKey(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class LoginAttemptState
    {
        public object SyncRoot { get; } = new();
        public List<DateTime> Failures { get; } = [];
        public DateTime BlockedUntilUtc { get; set; } = DateTime.MinValue;
    }

    private sealed record AdminSession(DateTime ExpiresAtUtc, string PlaybackToken);
}

public sealed class AuthLoginRequest
{
    public string Password { get; set; } = "";
}

public sealed class AuthSetupRequest
{
    public string Password { get; set; } = "";
}

public sealed class AuthPasswordChangeRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public sealed class PlaybackTokenRotateRequest
{
    public string CurrentPassword { get; set; } = "";
}

public sealed class PlaybackTokenAuthRequest
{
    public bool Enabled { get; set; } = true;
}
