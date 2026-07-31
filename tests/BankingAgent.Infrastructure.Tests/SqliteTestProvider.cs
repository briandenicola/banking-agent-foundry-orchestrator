using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BankingAgent.Infrastructure.Tests;

internal static class SqliteTestProvider
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsLinux())
        {
            NativeLibrary.SetDllImportResolver(
                typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
                ResolveLinuxSqlite);
        }

        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
    }

    private static IntPtr ResolveLinuxSqlite(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath) =>
        libraryName == "sqlite3"
            ? NativeLibrary.Load("libsqlite3.so.0", assembly, searchPath)
            : IntPtr.Zero;
}
