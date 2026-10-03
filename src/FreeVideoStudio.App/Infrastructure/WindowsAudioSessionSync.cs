// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// VOLSYNC_01 — the app's master volume and its entry in the Windows Volume Mixer are ONE control.
///
/// <para>Before: the app attenuated inside mpv and never touched its Windows audio session, so the
/// two volumes simply multiplied and neither ever moved when the other did.</para>
///
/// <para>Now this process's audio session (every player, the voice-over takes and the UI sounds share
/// it — one process, one default session) carries the master:</para>
/// <list type="bullet">
/// <item>app slider → session level = (position/100)³ (VOLCURVE_01's perceptual gain; the session
///   volume is LINEAR amplitude per Microsoft's "Audio-Tapered Volume Controls"), shared mute →
///   session mute;</item>
/// <item>Volume Mixer → app slider = ∛level × 100, Mixer mute → shared mute. Polled every
///   <see cref="PollMs"/> ms on one dedicated MTA thread that owns every COM call;</item>
/// <item>while it works, <see cref="MpvIpcClient.MasterAppliedBySystem"/> is true and players carry
///   only their balances, so the master is never applied twice. If Core Audio is unavailable (no
///   device, not Windows, any COM failure) the flag goes false and players apply the master
///   themselves — the old behaviour, with the new curve.</item>
/// </list>
/// <para>Raw vtable calls (the TaskbarProgress pattern): NativeAOT-safe, no ComWrappers.</para>
/// </summary>
internal static unsafe partial class WindowsAudioSessionSync
{
    public const int PollMs = 300;
    private const float Epsilon = 0.004f;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager = new("BFA971F1-4D5E-40BB-935E-967039BFBEE4");
    /// <summary>Our own event context, so a change we made is never mistaken for the user's.</summary>
    private static readonly Guid OurContext = new("6F1C2A3E-5B7D-4C21-9E0A-FVS000000001".Replace("FVS", "0A0"));

    private const uint CLSCTX_ALL = 0x17;
    private const uint COINIT_MULTITHREADED = 0x0;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private static Thread? _thread;
    private static volatile int _desiredLevel = -1;     // app position the session should reflect
    private static volatile int _desiredMuted = -1;     // 0/1, -1 = nothing pending
    private static int _suppressPosition = -1;          // a position that CAME from Windows: do not echo it back
    private static volatile bool _stop;

    public static void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread != null) return;
        MpvIpcClient.GlobalMasterVolumeChanged += OnAppMasterChanged;
        _desiredLevel = MpvIpcClient.GlobalMasterVolume;   // the app is the truth at startup
        _desiredMuted = MpvIpcClient.GlobalMuted ? 1 : 0;
        _thread = new Thread(Run) { IsBackground = true, Name = "FVS Windows audio session sync" };
        _thread.Start();
    }

    public static void Stop() => _stop = true;

    private static void OnAppMasterChanged(int level)
    {
        if (Interlocked.Exchange(ref _suppressPosition, -1) == level) return;
        _desiredLevel = level;
        _desiredMuted = MpvIpcClient.GlobalMuted ? 1 : 0;
    }

    private static void Run()
    {
        try { CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); Fallback("COM could not be initialised"); return; }

        IntPtr volume = IntPtr.Zero;
        long acquiredAt = 0;
        float lastLevel = -1f;
        int lastMute = -1;
        int failures = 0;

        while (!_stop)
        {
            try
            {
                // Re-acquire every few seconds: the default output device can change (headphones
                // plugged in), and the session volume is per device.
                if (volume == IntPtr.Zero || Environment.TickCount64 - acquiredAt > 3000)
                {
                    Release(ref volume);
                    volume = AcquireSimpleVolume();
                    acquiredAt = Environment.TickCount64;
                    lastLevel = -1f;
                    lastMute = -1;
                }

                if (volume == IntPtr.Zero)
                {
                    if (++failures == 10) Fallback("no default audio device / session volume");
                }
                else
                {
                    failures = 0;
                    MarkSystemApplied(true);

                    var vtbl = *(void***)volume;
                    var setMaster = (delegate* unmanaged[Stdcall]<IntPtr, float, Guid*, int>)vtbl[3];
                    var getMaster = (delegate* unmanaged[Stdcall]<IntPtr, float*, int>)vtbl[4];
                    var setMute = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, int>)vtbl[5];
                    var getMute = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtbl[6];
                    Guid ctx = OurContext;

                    int want = _desiredLevel;
                    if (want >= 0)
                    {
                        _desiredLevel = -1;
                        float level = (float)MpvIpcClient.PerceptualGain(want);
                        if (setMaster(volume, level, &ctx) >= 0) lastLevel = level;
                    }
                    int wantMute = _desiredMuted;
                    if (wantMute >= 0)
                    {
                        _desiredMuted = -1;
                        if (setMute(volume, wantMute, &ctx) >= 0) lastMute = wantMute;
                    }

                    float cur;
                    int mute;
                    if (getMaster(volume, &cur) >= 0 && getMute(volume, &mute) >= 0)
                    {
                        bool levelMoved = lastLevel >= 0 && Math.Abs(cur - lastLevel) > Epsilon;
                        bool muteMoved = lastMute >= 0 && (mute != 0 ? 1 : 0) != lastMute;
                        if (lastLevel < 0) lastLevel = cur;
                        if (lastMute < 0) lastMute = mute != 0 ? 1 : 0;
                        if (levelMoved || muteMoved)
                        {
                            lastLevel = cur;
                            lastMute = mute != 0 ? 1 : 0;
                            int position = (int)Math.Round(MpvIpcClient.PositionForGain(cur));
                            bool muted = mute != 0;
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (levelMoved && position != MpvIpcClient.GlobalMasterVolume)
                                {
                                    Interlocked.Exchange(ref _suppressPosition, position);
                                    MpvIpcClient.SetGlobalMasterVolume(position);
                                }
                                if (muteMoved) MpvIpcClient.SetGlobalMuted(muted);
                            });
                        }
                    }
                    else
                    {
                        Release(ref volume);
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.SwallowedThrottled(ex);
                Release(ref volume);
            }

            Thread.Sleep(PollMs);
        }
        Release(ref volume);
    }

    private static bool _systemApplied;

    private static void MarkSystemApplied(bool value)
    {
        if (_systemApplied == value) return;
        _systemApplied = value;
        Dispatcher.UIThread.Post(() => MpvIpcClient.SetMasterAppliedBySystem(value));
        RuntimeLog.Info("Audio", value
            ? "VOLSYNC_01: the master volume is carried by this app's Windows audio session (Volume Mixer in sync)."
            : "VOLSYNC_01: Windows audio session unavailable; players apply the master themselves.");
    }

    private static void Fallback(string why)
    {
        RuntimeLog.Info("Audio", $"VOLSYNC_01 fallback: {why}.");
        MarkSystemApplied(false);
    }

    /// <summary>Default render endpoint → IAudioSessionManager → this process's default session ISimpleAudioVolume.</summary>
    private static IntPtr AcquireSimpleVolume()
    {
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, manager = IntPtr.Zero, simple = IntPtr.Zero;
        try
        {
            if (CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, IID_IMMDeviceEnumerator, out enumerator) < 0
                || enumerator == IntPtr.Zero) return IntPtr.Zero;

            // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender = 0, eMultimedia = 1, out IMMDevice) — slot 4.
            var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)(*(void***)enumerator)[4];
            IntPtr dev;
            if (getDefault(enumerator, 0, 1, &dev) < 0 || dev == IntPtr.Zero) return IntPtr.Zero;
            device = dev;

            // IMMDevice::Activate(riid, dwClsCtx, pActivationParams, out ppInterface) — slot 3.
            var activate = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)(*(void***)device)[3];
            Guid iid = IID_IAudioSessionManager;
            IntPtr mgr;
            if (activate(device, &iid, CLSCTX_ALL, IntPtr.Zero, &mgr) < 0 || mgr == IntPtr.Zero) return IntPtr.Zero;
            manager = mgr;

            // IAudioSessionManager::GetSimpleAudioVolume(AudioSessionGuid = NULL, StreamFlags = 0, out) — slot 4.
            // NULL selects this process's default session — the one mpv, NAudio and WinMM all open.
            var getSimple = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr*, int>)(*(void***)manager)[4];
            IntPtr sv;
            if (getSimple(manager, null, 0, &sv) < 0 || sv == IntPtr.Zero) return IntPtr.Zero;
            simple = sv;
            IntPtr result = simple;
            simple = IntPtr.Zero;
            return result;
        }
        finally
        {
            Release(ref simple);
            Release(ref manager);
            Release(ref device);
            Release(ref enumerator);
        }
    }

    private static void Release(ref IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return;
        try
        {
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)unknown)[2];
            release(unknown);
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        unknown = IntPtr.Zero;
    }
}
