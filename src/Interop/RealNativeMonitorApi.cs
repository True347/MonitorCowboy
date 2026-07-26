using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MonitorCowboy.Interop;

/// <summary>
/// Production implementation over dxva2.dll / user32.dll.
///
/// Identity chain: GetPhysicalMonitorsFromHMONITOR alone only yields
/// "Generic PnP Monitor"-style descriptions, so enumeration also walks the
/// active display-config paths and joins them to each HMONITOR via the GDI
/// device name (MONITORINFOEX.szDevice == viewGdiDeviceName). Targets under
/// one source are paired with the physical monitor array index-wise, which
/// also covers clone mode (one source, several targets).
///
/// Handle policy: physical monitor handles are acquired fresh for every
/// operation and destroyed before it returns. Long-lived handles acquired at
/// startup have been observed to go permanently stale on some driver stacks
/// (every exchange failing with I2C error 0xC0262582 while other tools using
/// fresh handles work) — matching how the mature DDC tools operate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealNativeMonitorApi : INativeMonitorApi
{
    // Process-wide DDC serialization. Concurrent DDC/CI traffic to DIFFERENT
    // monitors is not safe either: displays on the same adapter (and monitors
    // daisy-chained over DisplayPort MST) share I2C/aux plumbing, and parallel
    // exchanges corrupt each other.
    private static readonly object DdcGate = new();

    public int LastWin32Error { get; private set; }

    public IReadOnlyList<PhysicalMonitorInfo> EnumerateMonitors()
    {
        try
        {
            return EnumerateCore();
        }
        catch
        {
            return [];
        }
    }

    public bool TryGetVcpFeature(MonitorRef monitor, byte code, out uint currentValue, out uint maximumValue)
    {
        uint current = 0;
        uint maximum = 0;
        var ok = WithMonitorHandle(monitor, handle =>
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, 0, out current, out maximum))
                return true;
            LastWin32Error = Marshal.GetLastWin32Error();
            return false;
        });

        currentValue = current;
        maximumValue = maximum;
        return ok;
    }

    public bool TrySetVcpFeature(MonitorRef monitor, byte code, uint value)
    {
        return WithMonitorHandle(monitor, handle =>
        {
            if (NativeMethods.SetVCPFeature(handle, code, value))
                return true;
            LastWin32Error = Marshal.GetLastWin32Error();
            return false;
        });
    }

    public bool TryGetCapabilitiesString(MonitorRef monitor, out string capabilities)
    {
        var caps = "";
        var ok = WithMonitorHandle(monitor, handle =>
        {
            if (!NativeMethods.GetCapabilitiesStringLength(handle, out var length) || length == 0)
            {
                LastWin32Error = Marshal.GetLastWin32Error();
                return false;
            }

            var buffer = new byte[length];
            if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
            {
                LastWin32Error = Marshal.GetLastWin32Error();
                return false;
            }

            var terminator = Array.IndexOf(buffer, (byte)0);
            caps = Encoding.ASCII.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
            return caps.Length > 0;
        });

        capabilities = caps;
        return ok;
    }

    /// <summary>
    /// Acquire the referenced monitor's handle fresh, run the operation under
    /// the process-wide DDC gate, and always destroy every acquired handle.
    /// </summary>
    private bool WithMonitorHandle(MonitorRef monitor, Func<nint, bool> operation)
    {
        try
        {
            lock (DdcGate)
            {
                var hMonitor = FindHmonitor(monitor.GdiDeviceName);
                if (hMonitor == 0)
                    return false;

                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                {
                    LastWin32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                if (monitor.Index >= count)
                    return false;

                var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                {
                    LastWin32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                try
                {
                    return operation(physicals[monitor.Index].hPhysicalMonitor);
                }
                finally
                {
                    foreach (var physical in physicals)
                        NativeMethods.DestroyPhysicalMonitor(physical.hPhysicalMonitor);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static nint FindHmonitor(string gdiDeviceName)
    {
        nint found = 0;
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            try
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                };
                if (NativeMethods.GetMonitorInfoW(hMonitor, ref info)
                    && string.Equals(info.szDevice, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    found = hMonitor;
                }
            }
            catch
            {
                // An exception must never cross the native callback boundary.
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return found;
    }

    private static List<PhysicalMonitorInfo> EnumerateCore()
    {
        var hmonitors = new List<(nint Handle, string GdiName)>();
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            try
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                };
                if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                    hmonitors.Add((hMonitor, info.szDevice));
            }
            catch
            {
                // An exception must never cross the native callback boundary.
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);

        var targetsBySource = QueryTargets();
        var result = new List<PhysicalMonitorInfo>();

        foreach (var (hMonitor, gdiName) in hmonitors)
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                continue;

            targetsBySource.TryGetValue(gdiName, out var targets);

            for (var i = 0; i < count; i++)
            {
                var target = targets is not null && i < targets.Count ? targets[i] : null;

                var friendly = target?.FriendlyName;
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = $"Display {result.Count + 1}";

                var devicePath = target?.DevicePath;
                if (string.IsNullOrWhiteSpace(devicePath))
                    devicePath = $"{gdiName}#{i}";

                result.Add(new PhysicalMonitorInfo(
                    new MonitorRef(gdiName, i),
                    devicePath,
                    friendly,
                    target?.IsInternal ?? false));
            }
        }

        return result;
    }

    private sealed record TargetInfo(string DevicePath, string FriendlyName, bool IsInternal);

    private static Dictionary<string, List<TargetInfo>> QueryTargets()
    {
        var map = new Dictionary<string, List<TargetInfo>>(StringComparer.OrdinalIgnoreCase);

        uint pathCount = 0;
        NativeMethods.DISPLAYCONFIG_PATH_INFO[]? paths = null;

        // The topology can change between sizing and querying; retry on the
        // resulting ERROR_INSUFFICIENT_BUFFER (122) like the SDK samples do.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QdcOnlyActivePaths, out pathCount, out var modeCount) != 0)
                return map;

            paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
            var rc = NativeMethods.QueryDisplayConfig(NativeMethods.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0);
            if (rc == 0)
                break;

            paths = null;
            if (rc != 122)
                return map;
        }

        if (paths is null)
            return map;

        for (var i = 0; i < pathCount; i++)
        {
            var source = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = NativeMethods.DeviceInfoGetSourceName,
                    size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
            };
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref source) != 0)
                continue;

            var target = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = NativeMethods.DeviceInfoGetTargetName,
                    size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id,
                },
            };
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref target) != 0)
                continue;

            var isInternal = target.outputTechnology
                is NativeMethods.OutputTechInternal
                or NativeMethods.OutputTechDisplayPortEmbedded
                or NativeMethods.OutputTechUdiEmbedded;

            if (!map.TryGetValue(source.viewGdiDeviceName, out var list))
                map[source.viewGdiDeviceName] = list = [];

            list.Add(new TargetInfo(target.monitorDevicePath, target.monitorFriendlyDeviceName, isInternal));
        }

        return map;
    }
}
