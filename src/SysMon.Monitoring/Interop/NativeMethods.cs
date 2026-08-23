using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysMon.Monitoring.Interop;

/// <summary>
/// Direct Win32 calls for the metrics that would otherwise cost far more through
/// PerformanceCounter or WMI. Everything here is cheap enough to run on every tick.
/// </summary>
internal static partial class NativeMethods
{
    internal const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemProcessorPerformanceInfo
    {
        internal long IdleTime;
        internal long KernelTime;
        internal long UserTime;
        internal long DpcTime;
        internal long InterruptTime;
        internal uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        internal uint dwLength;
        internal uint dwMemoryLoad;
        internal ulong ullTotalPhys;
        internal ulong ullAvailPhys;
        internal ulong ullTotalPageFile;
        internal ulong ullAvailPageFile;
        internal ulong ullTotalVirtual;
        internal ulong ullAvailVirtual;
        internal ulong ullAvailExtendedVirtual;
    }

    /// <summary>
    /// Kept blittable (a fixed char buffer rather than a marshalled string) so it can be used
    /// with a source-generated P/Invoke, which avoids per-call marshalling stubs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OsVersionInfoEx
    {
        internal uint dwOSVersionInfoSize;
        internal uint dwMajorVersion;
        internal uint dwMinorVersion;
        internal uint dwBuildNumber;
        internal uint dwPlatformId;

        internal fixed char szCSDVersion[128];

        internal ushort wServicePackMajor;
        internal ushort wServicePackMinor;
        internal ushort wSuiteMask;
        internal byte wProductType;
        internal byte wReserved;
    }

    /// <summary>
    /// Returns per-logical-processor idle/kernel/user times in one call. Taking deltas between
    /// ticks gives exact utilisation without the setup cost or allocations of PerformanceCounter.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Milliseconds since boot, including time spent asleep.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial ulong GetTickCount64();

    /// <summary>
    /// Maps a volume to the physical disk(s) it lives on. This is the reliable way to join a
    /// drive letter to its hardware; the sensor library's display name cannot be relied upon to
    /// mention drive letters at all.
    /// </summary>
    internal const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    /// <summary>
    /// Opens a volume for metadata queries. Desired access is deliberately zero: querying a
    /// volume's disk extents needs no read rights, so this works without elevation.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    /// <summary>
    /// The honest Windows version. GetVersionEx lies to unmanifested processes for compatibility;
    /// RtlGetVersion does not, which is what makes "Windows 11" reportable at all.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int RtlGetVersion(ref OsVersionInfoEx versionInfo);
}
