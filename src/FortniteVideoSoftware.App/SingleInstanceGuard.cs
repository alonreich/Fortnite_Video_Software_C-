using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

/// <summary>
/// ISSUE_03 — makes sure only ONE Main App process runs at a time.
///
/// WHY THIS IS NOT COSMETIC: <c>RecoveryManager.AcquireLock()</c> blindly overwrites
/// <c>app_session.lock</c> with its own PID, and <c>CleanupLock()</c> on a clean exit deletes
/// that lock AND calls <c>ClearState()</c>. With two Main App instances running, the first one
/// to close therefore wiped the second one's crash-recovery state, and both were writing the
/// same <c>recovery_v2.json</c> on top of each other the whole time. The user's protection
/// against a crash was silently gone.
///
/// SCOPE — this guard applies ONLY to the Main App. The Video Merger (<c>--merger</c>) and the
/// Crop Tools (<c>--crop-tool</c>) are, by architectural mandate, separate processes with their
/// own memory isolation, and the installer/uninstaller workers must obviously be able to run
/// alongside anything. See <see cref="AppliesTo"/> — do not widen it.
///
/// BEHAVIOUR when a second Main App launch happens:
///   * If it was started with a video file (Explorer "Open with"), the path is handed to the
///     already-running instance over a named pipe and loaded there. This is strictly better
///     than the old behaviour, where "Open with" spawned a rival process.
///   * Either way the running window is brought to the front and the new process exits quietly.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexNameBase = @"Local\FortniteVideoSoftware_MainApp_SingleInstance";
    private const string PipeNameBase = "FortniteVideoSoftware_MainApp_Handoff";
    private const int HandoffTimeoutMs = 2000;

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCts;

    private static string? _cachedUserScope;

    /// <summary>
    /// A stable, per-user suffix for kernel object names. The SID is preferred (it is unique and
    /// cannot collide); the user name is a fallback for the rare case where the SID is unavailable.
    /// </summary>
    private static string UserScope
    {
        get
        {
            if (_cachedUserScope != null) return _cachedUserScope;

            string scope;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    scope = identity.User?.Value ?? identity.Name;
                }
                else
                {
                    scope = Environment.UserName;
                }
            }
            catch
            {
                scope = Environment.UserName;
            }

            _cachedUserScope = scope.Replace('\\', '_');
            return _cachedUserScope;
        }
    }

    private static string MutexName => $"{MutexNameBase}_{UserScope}";
    private static string PipeName => $"{PipeNameBase}_{UserScope}";

    /// <summary>Raised on a background thread when another launch hands us a file path.</summary>
    public static event Action<string>? VideoPathReceived;

    /// <summary>
    /// True only for a Main App launch. Sub-applications and deployment workers are exempt:
    /// they are meant to run concurrently.
    ///
    /// NOTE: the caller additionally gates on the command being "run-ui" (or an Open-With file
    /// path). The headless CLI commands — paths, read-state, phase1-gate and friends — are
    /// diagnostics/CI gates that must keep working while the editor is open, so they never
    /// reach this guard.
    /// </summary>
    public static bool AppliesTo(string[] args)
    {
        foreach (string a in args)
        {
            if (a.Equals("--merger", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Attempts to become the one-and-only Main App.
    /// Returns true if this process owns the instance and should keep booting.
    /// Returns false if another instance already owns it — the caller must exit immediately
    /// WITHOUT touching the recovery lock or any shared state.
    /// </summary>
    public static bool TryAcquire(string[] args)
    {
        if (!AppliesTo(args)) return true;

        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException ex)
        {
            _mutex = ex.Mutex as Mutex ?? new Mutex(true, MutexName, out createdNew);
            createdNew = true;
            RuntimeLog.Fail("SingleInstance", "Acquired an abandoned instance mutex. The previous run crashed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Could not create the instance mutex: {ex.Message}. Continuing unguarded.");
            return true;
        }

        if (createdNew)
        {
            RuntimeLog.Info("SingleInstance", "This process owns the Main App instance.");
            StartHandoffListener();
            return true;
        }

        RuntimeLog.Info("SingleInstance", "Another Main App instance is already running — handing off and exiting.");

        string? pendingPath = OpenWithLaunch.PendingVideoPath;
        bool delivered = TrySendHandoff(pendingPath);

        if (!string.IsNullOrEmpty(pendingPath) && !delivered)
        {
            RuntimeLog.Fail("SingleInstance",
                "Could not hand the video path to the running instance. The user will need to load it manually.");
        }

        FocusExistingWindow();

        try { _mutex.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        _mutex = null;
        return false;
    }

    /// <summary>Releases the instance claim. Safe to call more than once.</summary>
    public static void Release()
    {
        // LEAK_02 — CANCEL ONLY, DELIBERATELY NOT DISPOSED. AUDITED, ACCEPTED, DO NOT "FIX".
        // Release() runs on the interface thread immediately before Environment.Exit(0). The
        // handoff listener is parked inside WaitForConnectionAsync(token), which registers a
        // callback on this token; CancellationTokenSource.Dispose() would block until that
        // callback completed and could hang the app on close for no gain whatsoever — the process
        // is about to end, and the OS reclaims the source either way.
        try { _listenerCts?.Cancel(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        _listenerCts = null;

        if (_mutex == null) return;
        try { _mutex.ReleaseMutex(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        try { _mutex.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        _mutex = null;
    }

    /// <summary>
    /// Background named-pipe server. One connection per handoff; the loop restarts after each
    /// so any number of later launches can hand off. All failures are swallowed — a broken
    /// listener must never disturb the running editor.
    /// </summary>
    private static void StartHandoffListener()
    {
        _listenerCts = new CancellationTokenSource();
        CancellationToken token = _listenerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = CreateSecuredPipeServer();

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    if (!IsConnectedClientTrusted(server))
                    {
                        RuntimeLog.Fail("SingleInstance",
                            "Rejected a handoff connection from a different user account.");
                        try { server.Disconnect(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                        continue;
                    }

                    using var reader = new StreamReader(server);
                    string? payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                    if (payload != null && payload.Length > 8192)
                    {
                        RuntimeLog.Fail("SingleInstance", "Rejected an implausibly large handoff payload.");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        string path = payload.Trim();
                        if (!IsAcceptableHandoffPath(path))
                        {
                            RuntimeLog.Fail("SingleInstance", "Rejected a handoff payload that was not a local video file path.");
                            continue;
                        }
                        if (File.Exists(path))
                        {
                            RuntimeLog.Info("SingleInstance", $"Received a video handoff from another launch: {Path.GetFileName(path)}");
                            RuntimeLog.Debug("SingleInstance", $"Handoff full path: {path}");
                            try { VideoPathReceived?.Invoke(path); } catch (Exception ex) { RuntimeLog.Fail("SingleInstance", ex); }
                        }
                        else
                        {
                            RuntimeLog.Fail("SingleInstance", "Handoff payload pointed at a file that no longer exists.");
                        }
                    }
                    else
                    {
                        try { VideoPathReceived?.Invoke(string.Empty); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("SingleInstance", $"Handoff listener error: {ex.Message}");
                    try { await Task.Delay(500, token).ConfigureAwait(false); } catch { return; }
                }
            }
        }, token);
    }

    /// <summary>
    /// ISSUE_13 — creates the handoff server.
    ///
    /// SECURITY MODEL, and why it is built this way:
    ///   1. The pipe NAME is scoped to the current user's SID (see <see cref="PipeName"/>), so a
    ///      different user's session cannot even guess at, let alone squat, this app's pipe.
    ///   2. Every accepted connection is IDENTITY-CHECKED against the current user before a single
    ///      byte is acted upon (see <see cref="IsConnectedClientTrusted"/>). This is the control
    ///      that actually matters: previously the listener acted on whatever arrived, from anyone,
    ///      so any local process could make the editor open a file of its choosing.
    ///   3. Payloads are length-capped and must look like a local video path
    ///      (<see cref="IsAcceptableHandoffPath"/>).
    ///
    /// NOTE ON THE ACL. A `PipeSecurity` DACL would be worthwhile defence-in-depth on top of the
    /// above, but `PipeSecurity`/`NamedPipeServerStreamAcl` live in the separate
    /// `System.IO.Pipes.AccessControl` NuGet package, which this project does not reference. Adding
    /// a package to the NativeAOT single-EXE build is not a change worth making blind, so it is
    /// deliberately left out: an untrusted local process can still CONNECT, but it is identified
    /// and disconnected without being read, which removes the exploitable behaviour. If that
    /// package is ever added, switch this method to `NamedPipeServerStreamAcl.Create` with a DACL
    /// granting the current user and LocalSystem only.
    /// </summary>
    private static NamedPipeServerStream CreateSecuredPipeServer()
    {
        return new NamedPipeServerStream(
            PipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>
    /// ISSUE_13 — confirms the connected client runs as the same user as this process. Belt and
    /// braces alongside the ACL: the listener used to act on whatever arrived, from anyone.
    /// </summary>
    private static bool IsConnectedClientTrusted(NamedPipeServerStream server)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            using WindowsIdentity self = WindowsIdentity.GetCurrent();
            string? expected = self.User?.Value;

            bool trusted = false;
            server.RunAsClient(() =>
            {
                using WindowsIdentity peer = WindowsIdentity.GetCurrent();
                trusted = expected != null && peer.User?.Value == expected;
            });

            return trusted;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Could not verify the handoff caller's identity: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// ISSUE_13 — a handoff payload is only ever an absolute local path to a video this app
    /// supports. Anything else (a URL, a UNC share, a relative path, a traversal attempt, a
    /// non-video extension) is refused before the editor is asked to open it.
    /// </summary>
    private static bool IsAcceptableHandoffPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.Length > 4096) return false;
            if (path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (!Path.IsPathFullyQualified(path)) return false;

            string full = Path.GetFullPath(path);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov";
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySendHandoff(string? videoPath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(HandoffTimeoutMs);

            using var writer = new StreamWriter(client);
            writer.Write(videoPath ?? string.Empty);
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Handoff to the running instance failed: {ex.Message}");
            return false;
        }
    }


    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    /// <summary>
    /// Best-effort: find the other instance's top-level window and raise it, so the user sees a
    /// response instead of a launch that appears to do nothing. Windows may refuse the
    /// foreground change (focus-stealing prevention) — in that case the taskbar button flashes,
    /// which is still feedback.
    /// </summary>
    private static void FocusExistingWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            string ownName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            uint ownPid = (uint)Environment.ProcessId;

            foreach (var proc in System.Diagnostics.Process.GetProcessesByName(ownName))
            {
                if ((uint)proc.Id == ownPid) continue;

                uint targetPid = (uint)proc.Id;
                IntPtr found = IntPtr.Zero;

                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == targetPid && IsWindowVisible(hWnd))
                    {
                        found = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    ShowWindow(found, SwRestore);
                    SetForegroundWindow(found);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Could not focus the running instance: {ex.Message}");
        }
    }
}
