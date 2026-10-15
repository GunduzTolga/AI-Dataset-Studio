using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using AIDatasetStudio.Application.Training;
using Microsoft.Win32;

namespace AIDatasetStudio.Infrastructure.Training;

/// <summary>
/// The GPU's memory use on Windows, whatever its maker (M29): AMD's driver has no rocm-smi on
/// Windows, but every driver reports to Windows' own "GPU Adapter Memory" performance counters,
/// which Task Manager shows too. The counters are asked for by their English names, which work
/// on a Turkish Windows as well; the card's name and memory size come from its driver's registry key.
/// </summary>
internal static class WindowsGpuMemory
{
    private const string DedicatedUsage = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint FormatLarge = 0x00000400;
    private const uint MoreData = 0x800007D2;

    /// <summary>
    /// The card with the most memory and the most memory any card has in use; <c>null</c> when
    /// Windows reports neither.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static GpuMemory? Read()
    {
        if (LargestAdapter() is not { } adapter || UsedBytes() is not { } used)
        {
            return null;
        }

        return new GpuMemory(adapter.Name, used / (1024 * 1024), adapter.Bytes / (1024 * 1024));
    }

    /// <summary>
    /// A memory size as the registry keeps it: a QWORD, a DWORD, or their bytes.
    /// </summary>
    internal static long? Bytes(object? value) => value switch
    {
        long qword => qword,
        int dword => (uint)dword,
        byte[] { Length: 8 } raw => BitConverter.ToInt64(raw),
        byte[] { Length: 4 } raw => BitConverter.ToUInt32(raw),
        _ => null,
    };

    [SupportedOSPlatform("windows")]
    private static (string? Name, long Bytes)? LargestAdapter()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
            (string? Name, long Bytes)? largest = null;
            foreach (var name in adapters?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var adapter = adapters!.OpenSubKey(name);
                    var bytes = Bytes(adapter?.GetValue("HardwareInformation.qwMemorySize")) ?? Bytes(adapter?.GetValue("HardwareInformation.MemorySize"));
                    if (bytes is > 0 && (largest is null || bytes > largest.Value.Bytes))
                    {
                        largest = (adapter!.GetValue("DriverDesc") as string, bytes.Value);
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // A key Windows keeps to itself ("Properties").
                }
            }

            return largest;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static long? UsedBytes()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0)
        {
            return null;
        }

        try
        {
            if (PdhAddEnglishCounterW(query, DedicatedUsage, IntPtr.Zero, out var counter) != 0 || PdhCollectQueryData(query) != 0)
            {
                return null;
            }

            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, FormatLarge, ref size, out _, IntPtr.Zero) != MoreData || size == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, FormatLarge, ref size, out var count, buffer) != 0)
                {
                    return null;
                }

                long? most = null;
                var itemSize = Marshal.SizeOf<CounterItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + (i * itemSize));
                    if (item.Status <= 1 && (most is null || item.Value > most))
                    {
                        most = item.Value;
                    }
                }

                return most;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W with a LONGLONG value: the instance name, the status, the value.
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public long Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
