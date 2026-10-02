
using System.Diagnostics;

namespace FreeVideoStudio.Core.Infrastructure;

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
        try
        {
            _mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            throw new LockException($"Named mutex '{name}' could not be opened: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// USERSCOPE_01 — a machine-wide ("Global\") name that is private to the current Windows user.
    /// It stays Global so the same user's processes in different sessions (console + RDP) still
    /// exclude each other over the per-user data root, while other accounts can never collide
    /// with it or be denied by its DACL.
    /// </summary>
    public static string UserScopedName(string baseName)
        => $@"Global\{baseName}_{FreeVideoStudio.Core.Ipc.IpcProtocol.UserScope}";

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
            catch (AbandonedMutexException swallowed)
            {
                guard._ownsHandle = true;
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
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
