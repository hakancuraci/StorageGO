using System.Reflection;
using System.Runtime.InteropServices;

namespace StorageGO;

internal static class NativeMethods
{
    private const string LibraryName = "storagego";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveLibrary);
    }

    internal static void EnsureInitialized() { }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint StorageGO_ABIVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_CreateClient(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string configJson);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_Invoke(
        NativeClientHandle clientId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string requestJson);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_CloseClient(ulong clientId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_OpenRead(
        NativeClientHandle clientId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_OpenWrite(
        NativeClientHandle clientId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int overwrite);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_ReadChunk(
        NativeTransferHandle transferId,
        [Out] byte[] buffer,
        uint capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_WriteChunk(
        NativeTransferHandle transferId,
        [In] byte[] buffer,
        uint count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_FlushTransfer(NativeTransferHandle transferId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StorageGO_CloseTransfer(ulong transferId, int forceSync);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void StorageGO_FreeResult(IntPtr result);

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var fileName = OperatingSystem.IsWindows()
            ? "storagego.dll"
            : OperatingSystem.IsLinux()
                ? "libstoragego.so"
                : throw new PlatformNotSupportedException("StorageGO supports Windows and Linux.");

        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("StorageGO native binaries currently support x64 processes only.");
        }

        var assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName),
            Path.Combine(assemblyDirectory, "runtimes", rid, "native", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(assemblyDirectory, fileName)
        ];

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        // Allows normal OS loader paths when the library is deployed externally.
        if (NativeLibrary.TryLoad(fileName, assembly, searchPath, out var fallback))
        {
            return fallback;
        }

        throw new DllNotFoundException(
            $"{fileName} bulunamadı. Native dosyayı runtimes/{rid}/native altına yerleştirin.");
    }
}
