namespace QaDocBackend.Infrastructure;

/// <summary>What one caller's allowance looks like right now, for the message the API sends back.</summary>
public record GenerationQuota(bool Allowed, int Limit, int Used, TimeSpan RetryAfter);

/// <summary>
/// Counts AI generations per person over a rolling hour (TestCaseGenerator:RateLimitPerHour,
/// default 5). It is a memory-local counter, like the feature needs: one API instance, one hour,
/// no new infrastructure.
/// </summary>
public interface IGenerationRateLimiter
{
    /// <summary>Records an attempt unless the caller is already over. Never throws.</summary>
    GenerationQuota Consume(int userId);
}

public class GenerationRateLimiter(TestCaseGeneratorSettings settings) : IGenerationRateLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<int, Queue<DateTime>> attempts = new();

    public GenerationQuota Consume(int userId)
    {
        int limit = Math.Max(1, settings.RateLimitPerHour);
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-1);

        lock (gate)
        {
            if (!attempts.TryGetValue(userId, out var times))
                attempts[userId] = times = new Queue<DateTime>();

            while (times.Count > 0 && times.Peek() <= cutoff) times.Dequeue();

            if (times.Count >= limit)
                return new GenerationQuota(Allowed: false, limit, times.Count, times.Peek().AddHours(1) - now);

            times.Enqueue(now);
            return new GenerationQuota(Allowed: true, limit, times.Count, TimeSpan.Zero);
        }
    }
}

public interface IQcGenerationRateLimiter : IGenerationRateLimiter;

public class QcGenerationRateLimiter(DocumentGeneratorSettings settings) : IQcGenerationRateLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<int, Queue<DateTime>> attempts = new();

    public GenerationQuota Consume(int userId)
    {
        int limit = Math.Max(1, settings.RateLimitPerHour);
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-1);

        lock (gate)
        {
            if (!attempts.TryGetValue(userId, out var times))
                attempts[userId] = times = new Queue<DateTime>();

            while (times.Count > 0 && times.Peek() <= cutoff) times.Dequeue();

            if (times.Count >= limit)
                return new GenerationQuota(Allowed: false, limit, times.Count, times.Peek().AddHours(1) - now);

            times.Enqueue(now);
            return new GenerationQuota(Allowed: true, limit, times.Count, TimeSpan.Zero);
        }
    }
}
