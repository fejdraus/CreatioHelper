namespace CreatioHelper.Shared.Utils;

public static class StatePolling
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    public const int DefaultMaxAttempts = 240;
        public static async Task<bool> WaitForStateAsync(
        Func<CancellationToken, Task<string?>> readState,
        string desiredState,
        Action<string?>? onWaiting = null,
        TimeSpan? interval = null,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readState);
        ArgumentException.ThrowIfNullOrEmpty(desiredState);
        var delay = interval ?? DefaultInterval;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var currentState = await readState(cancellationToken).ConfigureAwait(false);
            if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            onWaiting?.Invoke(currentState);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }
}