using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MonoMod.Utils;

namespace BepInEx.Preloader.Core;

internal static class PlatformUtils
{
    public static readonly bool ProcessIs64Bit = IntPtr.Size >= 8;
    public static Version WindowsVersion { get; set; }
    public static string WineVersion { get; set; }

    public static string LinuxArchitecture { get; set; }
    public static string LinuxKernelVersion { get; set; }

    /// <summary>
    ///     macOS product version (e.g. "14.5"), or null if it couldn't be read.
    /// </summary>
    public static string MacOSVersion { get; private set; }

    /// <summary>
    ///     Darwin kernel release (e.g. "23.5.0"). Unlike <see cref="MacOSVersion" />, it isn't shimmed for games built
    ///     against older SDKs, which see macOS 11+ as "10.16" and macOS 26 as "16.0".
    /// </summary>
    public static string MacOSKernelVersion { get; private set; }

    /// <summary>
    ///     True when this is an x86_64 process translated by Rosetta 2 on Apple Silicon.
    /// </summary>
    public static bool RosettaTranslated { get; private set; }

    /// <summary>
    ///     CPU architecture of the current process ("x86", "x64", "ARM", "ARM64"), or null if it wasn't detected.
    /// </summary>
    public static string ProcessArchitecture { get; private set; }

    [DllImport("libc.so.6", EntryPoint = "uname", CallingConvention = CallingConvention.Cdecl,
               CharSet = CharSet.Ansi)]
    private static extern IntPtr uname_linux(ref utsname_linux utsname);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "uname", CallingConvention = CallingConvention.Cdecl,
               CharSet = CharSet.Ansi)]
    private static extern IntPtr uname_osx(ref utsname_osx utsname);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "sysctlbyname", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sysctlbyname_int(string name, ref int oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "sysctlbyname", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sysctlbyname_bytes(string name, byte[] oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern bool RtlGetVersion(ref WindowsOSVersionInfoExW versionInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string libraryName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    private static bool Is(this Platform current, Platform expected) => (current & expected) == expected;

    /// <summary>
    ///     Recreation of MonoMod's PlatformHelper.DeterminePlatform method, but with libc calls instead of creating processes.
    /// </summary>
    public static void SetPlatform()
    {
        var current = Platform.Unknown;

        // For old Mono, get from a private property to accurately get the platform.
        // static extern PlatformID Platform
        var p_Platform = typeof(Environment).GetProperty("Platform", BindingFlags.NonPublic | BindingFlags.Static);
        string platID;
        if (p_Platform != null)
            platID = p_Platform.GetValue(null, new object[0]).ToString();
        else
            // For .NET and newer Mono, use the usual value.
            platID = Environment.OSVersion.Platform.ToString();
        platID = platID.ToLowerInvariant();

        if (platID.Contains("win"))
            current = Platform.Windows;
        else if (platID.Contains("mac") || platID.Contains("osx"))
            current = Platform.MacOS;
        else if (platID.Contains("lin") || platID.Contains("unix"))
            current = Platform.Linux;

#if NETSTANDARD2_0
        // .NET Core reports PlatformID.Unix on macOS for compatibility with Mono
        if (current == Platform.Linux && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            current = Platform.MacOS;
#endif

        if (current.Is(Platform.Linux) && Directory.Exists("/data") && File.Exists("/system/build.prop"))
            current = Platform.Android;
        else if (current.Is(Platform.Unix) && Directory.Exists("/System/Library/AccessibilityBundles"))
            current = Platform.iOS;

        if (current.Is(Platform.Windows))
        {
            var windowsVersionInfo = new WindowsOSVersionInfoExW();
            RtlGetVersion(ref windowsVersionInfo);

            WindowsVersion = new Version((int) windowsVersionInfo.dwMajorVersion,
                                         (int) windowsVersionInfo.dwMinorVersion, 0,
                                         (int) windowsVersionInfo.dwBuildNumber);

            var ntDll = LoadLibrary("ntdll.dll");
            if (ntDll != IntPtr.Zero)
            {
                var wineGetVersion = GetProcAddress(ntDll, "wine_get_version");
                if (wineGetVersion != IntPtr.Zero)
                {
                    current |= Platform.Wine;
                    // It's not safe to use the AsDelegate() extension method here because:
                    //  - It comes from the MonoMod.Utils.DynDll class, defined in MonoMod.Common.
                    //  - The DynDll class has a static constructor that reads PlatformHelper.Current.
                    //  - Reading from that property freezes it: subsequent writes will throw an exception.
                    //  - This method only sets PlatformHelper.Current at the very end.
                    var getVersion = Marshal.GetDelegateForFunctionPointer(wineGetVersion, typeof(GetWineVersionDelegate)) as GetWineVersionDelegate;
                    WineVersion = Marshal.PtrToStringAnsi(getVersion());
                }
            }
        }

        // Is64BitOperatingSystem has been added in .NET Framework 4.0
        var m_get_Is64BitOperatingSystem =
            typeof(Environment).GetProperty("Is64BitOperatingSystem")?.GetGetMethod();
        if (m_get_Is64BitOperatingSystem != null)
            current |= (bool) m_get_Is64BitOperatingSystem.Invoke(null, new object[0]) ? Platform.Bits64 : 0;
        else
            current |= IntPtr.Size >= 8 ? Platform.Bits64 : 0;

        if (current.Is(Platform.MacOS) || current.Is(Platform.Linux))
        {
            string arch = null;
            IntPtr result;

            try
            {
                if (current.Is(Platform.MacOS))
                {
                    var utsname_osx = new utsname_osx();
                    result = uname_osx(ref utsname_osx);
                    arch = utsname_osx.machine;

                    MacOSKernelVersion = utsname_osx.release;
                }
                else
                {
                    // Linux
                    var utsname_linux = new utsname_linux();
                    result = uname_linux(ref utsname_linux);
                    arch = utsname_linux.machine;

                    LinuxArchitecture = utsname_linux.machine;
                    LinuxKernelVersion = utsname_linux.version;
                }

                if (result != IntPtr.Zero)
                    arch = null;
            }
            catch (Exception) when (GetRuntimeProcessArchitecture() != null)
            {
                // No usable libc (e.g. musl without libc.so.6), but the runtime reports the architecture itself.
                // Mono can't, so there the exception still stops BepInEx rather than guessing the wrong detour platform.
            }

            if (current.Is(Platform.MacOS))
            {
                MacOSVersion = GetMacOSProductVersion();
                RosettaTranslated = IsRosettaTranslated();
            }

            // Rosetta 2 only runs x86_64 code; check it explicitly instead of trusting uname inside a translated process
            if (RosettaTranslated)
                ProcessArchitecture = "x64";
            else
                ProcessArchitecture = GetRuntimeProcessArchitecture() ?? ArchitectureFromUname(arch);

            if (ProcessArchitecture is "ARM" or "ARM64")
                current |= Platform.ARM;
        }
        else
        {
            // Detect ARM based on PE info or uname.
            typeof(object).Module.GetPEKind(out var peKind, out var machine);
            if (machine == (ImageFileMachine) 0x01C4 /* ARM, .NET Framework 4.5 */)
                current |= Platform.ARM;
        }

        PlatformHelper.Current = current;
    }

    private static string GetRuntimeProcessArchitecture()
    {
#if NETSTANDARD2_0
        // CoreCLR knows its own architecture; Mono keeps using uname as it always has
        if (Type.GetType("Mono.Runtime") == null)
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86   => "x86",
                Architecture.X64   => "x64",
                Architecture.Arm   => "ARM",
                Architecture.Arm64 => "ARM64",
                var other          => other.ToString()
            };
        }
#endif
        return null;
    }

    private static bool IsRosettaTranslated()
    {
        try
        {
            // sysctl.proc_translated is 1 under Rosetta 2, 0 for native processes and missing on Intel Macs
            var translated = 0;
            var size = (UIntPtr) sizeof(int);
            return sysctlbyname_int("sysctl.proc_translated", ref translated, ref size, IntPtr.Zero, UIntPtr.Zero) == 0 &&
                   translated == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string GetMacOSProductVersion()
    {
        try
        {
            // kern.osproductversion exists since macOS 10.13.4
            var buffer = new byte[64];
            var size = (UIntPtr) buffer.Length;
            if (sysctlbyname_bytes("kern.osproductversion", buffer, ref size, IntPtr.Zero, UIntPtr.Zero) != 0)
                return null;
            var length = Array.IndexOf(buffer, (byte) 0);
            return Encoding.ASCII.GetString(buffer, 0, length < 0 ? (int) size.ToUInt32() : length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ArchitectureFromUname(string machine)
    {
        if (string.IsNullOrEmpty(machine))
            return null;

        // uname reports the kernel's architecture, so a 32-bit process on a 64-bit kernel still sees the 64-bit name
        var is64BitProcess = IntPtr.Size >= 8;
        if (machine.StartsWith("aarch64") || machine.StartsWith("arm64"))
            return is64BitProcess ? "ARM64" : "ARM";
        if (machine.StartsWith("arm"))
            return "ARM";
        if (machine == "x86_64" || machine == "amd64")
            return is64BitProcess ? "x64" : "x86";
        if (machine is "i386" or "i486" or "i586" or "i686")
            return "x86";
        return machine;
    }

    // Returns a static string owned by Wine, so it must not be marshalled as string (the marshaller would free it)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetWineVersionDelegate();

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    public struct WindowsOSVersionInfoExW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;

        public WindowsOSVersionInfoExW()
        {
            dwOSVersionInfoSize = (uint) Marshal.SizeOf(typeof(WindowsOSVersionInfoExW));
            dwMajorVersion = 0;
            dwMinorVersion = 0;
            dwBuildNumber = 0;
            dwPlatformId = 0;
            szCSDVersion = null;
            wServicePackMajor = 0;
            wServicePackMinor = 0;
            wSuiteMask = 0;
            wProductType = 0;
            wReserved = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct utsname_osx
    {
        private const int osx_utslen = 256;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = osx_utslen)]
        public string sysname;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = osx_utslen)]
        public string nodename;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = osx_utslen)]
        public string release;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = osx_utslen)]
        public string version;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = osx_utslen)]
        public string machine;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct utsname_linux
    {
        private const int linux_utslen = 65;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = linux_utslen)]
        public string sysname;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = linux_utslen)]
        public string nodename;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = linux_utslen)]
        public string release;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = linux_utslen)]
        public string version;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = linux_utslen)]
        public string machine;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = linux_utslen)]
        public string domainname;
    }
}
