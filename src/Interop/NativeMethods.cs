using System.Runtime.InteropServices;

namespace MonitorCowboy.Interop;

internal static class NativeMethods
{
    public const uint QdcOnlyActivePaths = 2;
    public const uint DeviceInfoGetSourceName = 1;
    public const uint DeviceInfoGetTargetName = 2;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that identify built-in panels.
    public const uint OutputTechInternal = 0x80000000;
    public const uint OutputTechDisplayPortEmbedded = 11;
    public const uint OutputTechUdiEmbedded = 13;

    // --- dxva2.dll: physical monitor enumeration + Low-Level Monitor Configuration ---

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(nint hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitor(nint hPhysicalMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(nint hPhysicalMonitor, byte vcpCode, nint pvct, out uint currentValue, out uint maximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(nint hPhysicalMonitor, byte vcpCode, uint newValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetCapabilitiesStringLength(nint hPhysicalMonitor, out uint length);

    // The capabilities string is ANSI (LPSTR); a byte buffer is deliberate.
    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(nint hPhysicalMonitor, [Out] byte[] asciiCapabilities, uint lengthInCharacters);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public nint hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    // --- user32.dll: HMONITOR enumeration ---

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    // --- user32.dll: display configuration (device path / friendly name / output technology) ---

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable; // BOOL
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Opaque: the mode contents are never read, but QueryDisplayConfig needs an
    // array with the exact native element size (a 64-byte union in the SDK).
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }
}
