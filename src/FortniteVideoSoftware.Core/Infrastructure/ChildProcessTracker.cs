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
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
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

        // ISSUE_4: the free used to sit as a plain statement after the two calls below. If either
        // StructureToPtr or SetInformationJobObject threw, the block was never returned. It is the
        // only unmanaged allocation in the codebase without a guaranteed release — every other one
        // (MpvVideoView, GpuCapabilityProbe, KnownFolders) already uses try/finally.
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
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
    private static readonly IProcessJobTracker _tracker = OperatingSystem.IsWindows()
        ? new WindowsJobObjectTracker()
        : new NoOpJobTracker();

    public static void AddProcess(Process process) => _tracker.AddProcess(process);
}
