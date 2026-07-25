using System.Diagnostics;

namespace FortniteVideoSoftware.Core.Infrastructure;

public class LockException : Exception
{
    public LockException(string message) : base(message) { }
}

public sealed class NamedSystemMutex : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsHandle;

    private NamedSystemMutex(string name)
    {
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    public static NamedSystemMutex Acquire(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        NamedSystemMutex guard = new(name);
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (guard._mutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    guard._ownsHandle = true;
                    return guard;
                }
            }
            catch (AbandonedMutexException)
            {
                guard._ownsHandle = true;
                return guard;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                guard.Dispose();
                throw new LockException($"Timed out waiting for named mutex '{name}' after {timeout.TotalSeconds:0.0}s.");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }
    }

    public void Dispose()
    {
        if (_ownsHandle)
        {
            _mutex.ReleaseMutex();
            _ownsHandle = false;
        }

        _mutex.Dispose();
    }
}
