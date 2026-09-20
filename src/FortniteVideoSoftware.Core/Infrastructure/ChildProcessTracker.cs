using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.Core.Infrastructure;

public interface IProcessJobTracker
{
    void AddProcess(Process process);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class WindowsJobObjectTracker : IProcessJobTracker
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private readonly IntPtr _jobHandle;

    public WindowsJobObjectTracker()
    {
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = 0x2000
        };
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
            SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    public void AddProcess(Process process)
    {
        if (_jobHandle != IntPtr.Zero && process != null && !process.HasExited)
        {
            try
            {
                AssignProcessToJobObject(_jobHandle, process.Handle);
            }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

public class NoOpJobTracker : IProcessJobTracker
{
    public void AddProcess(Process process)
    {
    }
}

public static class ChildProcessTracker
{
    private static readonly IProcessJobTracker _tracker = CreateTracker();

    /// <summary>True when the real Windows Job Object tracker is active.</summary>
    public static bool IsJobObjectActive { get; private set; }

    private static IProcessJobTracker CreateTracker()
    {
        if (!OperatingSystem.IsWindows()) return new NoOpJobTracker();
        try
        {
            var tracker = new WindowsJobObjectTracker();
            IsJobObjectActive = true;
            return tracker;
        }
        catch (System.Exception ex)
        {
            CoreLogger.Info("ChildProcessTracker", $"Job object unavailable, degrading to no-op: {ex.GetType().Name}: {ex.Message}");
            CoreLogger.Swallowed(ex);
            IsJobObjectActive = false;
            return new NoOpJobTracker();
        }
    }

    public static void AddProcess(Process process)
    {
        try { _tracker.AddProcess(process); }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }
}
