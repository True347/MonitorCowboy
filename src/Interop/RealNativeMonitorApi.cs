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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealNativeMonitorApi : INativeMonitorApi
{
    public IReadOnlyList<PhysicalMonitorHandle> EnumerateMonitors()
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

    public bool TryGetVcpFeature(nint handle, byte code, out uint currentValue, out uint maximumValue)
    {
        currentValue = 0;
        maximumValue = 0;
        try
        {
            return NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, 0, out currentValue, out maximumValue);
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetVcpFeature(nint handle, byte code, uint value)
    {
        try
        {
            return NativeMethods.SetVCPFeature(handle, code, value);
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetCapabilitiesString(nint handle, out string capabilities)
    {
        capabilities = "";
        try
        {
            if (!NativeMethods.GetCapabilitiesStringLength(handle, out var length) || length == 0)
                return false;

            var buffer = new byte[length];
            if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
                return false;

            var terminator = Array.IndexOf(buffer, (byte)0);
            capabilities = Encoding.ASCII.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
            return capabilities.Length > 0;
        }
        catch
        {
            capabilities = "";
            return false;
        }
    }

    public void DestroyMonitor(nint handle)
    {
        try
        {
            NativeMethods.DestroyPhysicalMonitor(handle);
        }
        catch
        {
            // Handle cleanup is best-effort.
        }
    }

    private static List<PhysicalMonitorHandle> EnumerateCore()
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
        var result = new List<PhysicalMonitorHandle>();

        foreach (var (hMonitor, gdiName) in hmonitors)
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                continue;

            var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                continue;

            targetsBySource.TryGetValue(gdiName, out var targets);

            for (var i = 0; i < physicals.Length; i++)
            {
                var target = targets is not null && i < targets.Count ? targets[i] : null;

                var friendly = target?.FriendlyName;
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = physicals[i].szPhysicalMonitorDescription?.Trim();
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = $"Display {result.Count + 1}";

                var devicePath = target?.DevicePath;
                if (string.IsNullOrWhiteSpace(devicePath))
                    devicePath = $"{gdiName}#{i}";

                result.Add(new PhysicalMonitorHandle(
                    physicals[i].hPhysicalMonitor,
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

        if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            return map;

        var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
        if (NativeMethods.QueryDisplayConfig(NativeMethods.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0)
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
