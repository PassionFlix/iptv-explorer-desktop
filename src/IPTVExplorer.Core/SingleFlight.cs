namespace IPTVExplorer.Core;

/// <summary>Caller cancellation abandons only its wait, never starts a second copy of shared I/O.</summary>
public sealed class SingleFlight<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Task<TValue>> _running = new();

    public Task<TValue> RunAsync(TKey key, Func<Task<TValue>> operation, CancellationToken token)
    {
        Task<TValue> task;
        lock (_gate)
        {
            if (!_running.TryGetValue(key, out task!))
            {
                task = Task.Run(async () =>
                {
                    try { return await operation(); }
                    finally { lock (_gate) _running.Remove(key); }
                });
                _running.Add(key, task);
            }
        }
        return task.WaitAsync(token);
    }
}
