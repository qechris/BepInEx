using System.Runtime.InteropServices;

namespace BepInEx.Unity.IL2CPP.Hook;

/// <summary>
///     Follows jump stubs to the real function body, since Dobby aborts (or overwrites the following code) when asked to
///     patch a stub that is shorter than its detour jump. No-op when the pointer is already a function body.
/// </summary>
internal static class ExportThunkResolver
{
    private const int MaxHops = 8;

    public static nint Follow(nint func) => Follow(func, RuntimeInformation.ProcessArchitecture);

    internal static nint Follow(nint func, Architecture architecture)
    {
        if (func == 0) return func;

        return architecture switch
        {
            Architecture.X86   => FollowX86(func, false),
            Architecture.X64   => FollowX86(func, true),
            Architecture.Arm64 => FollowArm64(func),
            _                  => func
        };
    }

    /// <summary>
    ///     PE exports are often a <c>jmp rel32</c> (or <c>jmp [rip+disp]</c>) stub with int3 padding.
    /// </summary>
    private static nint FollowX86(nint func, bool is64Bit)
    {
        var fn = func;
        for (var hops = 0; hops < MaxHops; hops++)
        {
            byte op;
            try { op = Marshal.ReadByte(fn); }
            catch { return fn; }

            if (op == 0xE9)
            {
                fn += 5 + Marshal.ReadInt32(fn + 1);
                continue;
            }

            if (op == 0xFF && Marshal.ReadByte(fn + 1) == 0x25)
            {
                if (is64Bit)
                    fn = Marshal.ReadIntPtr(fn + 6 + Marshal.ReadInt32(fn + 2));
                else
                    fn = Marshal.ReadIntPtr((nint)(uint)Marshal.ReadInt32(fn + 2));
                continue;
            }

            break;
        }

        return fn;
    }

    /// <summary>
    ///     A single <c>b imm26</c> is 4 bytes, but an ARM64 detour needs up to 16.
    /// </summary>
    private static nint FollowArm64(nint func)
    {
        var fn = func;
        for (var hops = 0; hops < MaxHops; hops++)
        {
            uint insn;
            try { insn = unchecked((uint)Marshal.ReadInt32(fn)); }
            catch { return fn; }

            // B imm26: offset is imm26 * 4, sign-extended
            if ((insn & 0xFC000000) != 0x14000000)
                break;

            fn += (nint)(((long)(insn & 0x03FFFFFF) << 38) >> 36);
        }

        return fn;
    }
}
