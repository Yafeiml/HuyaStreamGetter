#nullable enable

using System.Collections.Concurrent;

/// <summary>
/// 按连接来源限制错误播放令牌的连续尝试。正确的 M3U/HLS/分片请求不进入计数，
/// 避免正常播放过程中大量请求触发限速。
/// </summary>
public sealed class PlaybackTokenRateLimiter
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public bool TryValidate(
        string? token,
        string encodedHash,
        string clientKey,
        out bool rateLimited,
        out TimeSpan retryAfter)
    {
        rateLimited = false;
        retryAfter = TimeSpan.Zero;
        DateTime now = DateTime.UtcNow;

        if (_attempts.TryGetValue(clientKey, out AttemptState? existing))
        {
            lock (existing.SyncRoot)
            {
                if (existing.BlockedUntilUtc > now)
                {
                    rateLimited = true;
                    retryAfter = existing.BlockedUntilUtc - now;
                    return false;
                }

                existing.Failures.RemoveAll(at => now - at > AttemptWindow);
            }
        }

        bool valid = PlaybackTokenProtector.ValidateToken(token, encodedHash);
        if (valid)
        {
            _attempts.TryRemove(clientKey, out _);
            return true;
        }

        AttemptState state = _attempts.GetOrAdd(clientKey, _ => new AttemptState());
        lock (state.SyncRoot)
        {
            now = DateTime.UtcNow;
            if (state.BlockedUntilUtc > now)
            {
                rateLimited = true;
                retryAfter = state.BlockedUntilUtc - now;
                return false;
            }

            state.Failures.RemoveAll(at => now - at > AttemptWindow);
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

    public void ResetAll() => _attempts.Clear();

    private sealed class AttemptState
    {
        public object SyncRoot { get; } = new();
        public List<DateTime> Failures { get; } = [];
        public DateTime BlockedUntilUtc { get; set; } = DateTime.MinValue;
    }
}
