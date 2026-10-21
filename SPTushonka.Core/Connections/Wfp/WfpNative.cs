using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SPT.GameConnections;

// Layouts follow Windows SDK fwptypes.h/fwpmtypes.h. Only the Windows x64 ABI is supported.
internal static class WfpNative
{
    internal const uint NotFound = 0x80320003; // FWP_E_FILTER_NOT_FOUND
    internal const uint ProviderNotFound = 0x80320005;
    internal const uint SublayerNotFound = 0x80320007;
    internal const uint AlreadyExists = 0x80320009;
    internal const uint Persistent = 1;
    internal const uint Block = 0x1001;
    internal const uint Permit = 0x1002;
    internal static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    internal static readonly Guid RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    internal static readonly Guid RemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    internal static readonly Guid Protocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    [StructLayout(LayoutKind.Sequential)]
    internal struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Display
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Provider
    {
        public Guid Key;
        public Display Display;
        public uint Flags;
        public Blob Data;
        public IntPtr ServiceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Sublayer
    {
        public Guid Key;
        public Display Display;
        public uint Flags;
        public IntPtr Provider;
        public Blob Data;
        public ushort Weight;
    }

    // FWP_VALUE0/FWP_CONDITION_VALUE0 share a 4-byte discriminator + aligned 8-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct Value
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public byte UInt8;

        [FieldOffset(8)]
        public ushort UInt16;

        [FieldOffset(8)]
        public uint UInt32;

        [FieldOffset(8)]
        public IntPtr Pointer;

        internal static Value U8(byte value) => new() { Type = 1, UInt8 = value };

        internal static Value U16(ushort value) => new() { Type = 2, UInt16 = value };

        internal static Value U32(uint value) => new() { Type = 3, UInt32 = value };

        internal static Value Ptr(uint type, IntPtr pointer) => new() { Type = type, Pointer = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Condition
    {
        public Guid Key;
        public uint Match;
        public Value Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Action
    {
        public uint Type;
        public Guid Key;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct Context
    {
        [FieldOffset(0)]
        public ulong Raw;

        [FieldOffset(0)]
        public Guid Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Filter
    {
        public Guid Key;
        public Display Display;
        public uint Flags;
        public IntPtr Provider;
        public Blob Data;
        public Guid Layer;
        public Guid Sublayer;
        public Value Weight;
        public uint ConditionCount;
        public IntPtr Conditions;
        public Action Action;
        public Context Context;
        public IntPtr Reserved;
        public ulong Id;
        public Value EffectiveWeight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint FwpmEngineOpen0(string? server, uint authnService, IntPtr auth, IntPtr session, out IntPtr engine);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmEngineClose0(IntPtr engine);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmTransactionCommit0(IntPtr engine);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmTransactionAbort0(IntPtr engine);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint FwpmGetAppIdFromFileName0(string name, out IntPtr blob);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern void FwpmFreeMemory0(ref IntPtr memory);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmProviderAdd0(IntPtr engine, ref Provider provider, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmProviderGetByKey0(IntPtr engine, in Guid key, out IntPtr provider);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmProviderDeleteByKey0(IntPtr engine, in Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmSubLayerAdd0(IntPtr engine, ref Sublayer sublayer, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmSubLayerGetByKey0(IntPtr engine, in Guid key, out IntPtr sublayer);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, in Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr securityDescriptor, out ulong id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterDeleteByKey0(IntPtr engine, in Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr handle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterEnum0(IntPtr engine, IntPtr handle, uint requested, out IntPtr entries, out uint returned);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engine, IntPtr handle);

    internal static void Check(uint code, string operation)
    {
        if (code != 0)
            throw new Win32Exception(unchecked((int)code), $"{operation} failed (0x{code:x8}).");
    }

    internal sealed class Engine : IDisposable
    {
        internal IntPtr Handle { get; private set; }

        internal Engine()
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("Game connection protection requires Windows x64.");
            Check(
                FwpmEngineOpen0(
                    null,
                    10 /* RPC_C_AUTHN_WINNT */
                    ,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var handle
                ),
                "Open filtering engine"
            );
            Handle = handle;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                FwpmEngineClose0(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    internal sealed class Arena : IDisposable
    {
        private readonly List<IntPtr> allocations = new();

        internal IntPtr Bytes(byte[] data)
        {
            var pointer = Marshal.AllocHGlobal(data.Length);
            allocations.Add(pointer);
            Marshal.Copy(data, 0, pointer, data.Length);
            return pointer;
        }

        internal IntPtr Structure<T>(T value)
            where T : struct
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            allocations.Add(pointer);
            Marshal.StructureToPtr(value, pointer, false);
            return pointer;
        }

        internal IntPtr Conditions(Condition[] values)
        {
            var size = Marshal.SizeOf<Condition>();
            var pointer = Marshal.AllocHGlobal(size * values.Length);
            allocations.Add(pointer);
            for (var i = 0; i < values.Length; i++)
                Marshal.StructureToPtr(values[i], pointer + size * i, false);
            return pointer;
        }

        public void Dispose()
        {
            foreach (var pointer in allocations)
                Marshal.FreeHGlobal(pointer);
            allocations.Clear();
        }
    }
}
