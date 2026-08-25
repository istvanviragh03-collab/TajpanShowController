namespace TajpanShowController.Core.Services;

public sealed class RetryPolicy(int maxAttempts)
{
    public int MaxAttempts { get; } = maxAttempts > 0 ? maxAttempts : throw new ArgumentOutOfRangeException(nameof(maxAttempts));
    public async Task<bool> ExecuteAsync(Func<int, CancellationToken, Task<bool>> operation, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            if (await operation(attempt, cancellationToken)) return true;
        return false;
    }
}
