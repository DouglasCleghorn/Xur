using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Xur.IO;

/// <summary>Small parameterized SQLite queries using the host library; compatible with Native AOT.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class NativeSqlite : IDisposable
{
    nint database;
    public NativeSqlite(string path, bool readOnly = false)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("SQLite paths cannot be symlinks.");
        var result = Open(path, out database, readOnly ? 1 : 2 | 4, 0);
        if (result != 0) { Dispose(); throw new IOException("Could not open SQLite database."); }
        BusyTimeout(database, 5000);
    }
    public List<string?[]> Query(string sql, params string?[] values)
    {
        nint statement = 0;
        try
        {
            Check(Prepare(database, sql, -1, out statement, 0));
            for (var index = 0; index < values.Length; index++)
                Check(values[index] is { } value ? BindText(statement, index + 1, value, -1, -1) : BindNull(statement, index + 1));
            var rows = new List<string?[]>(); int result;
            while ((result = Step(statement)) == 100)
            {
                var row = new string?[ColumnCount(statement)];
                for (var column = 0; column < row.Length; column++) row[column] = Marshal.PtrToStringUTF8(Text(statement, column));
                rows.Add(row);
            }
            if (result != 101) throw new IOException("SQLite query failed.");
            return rows;
        }
        finally { if (statement != 0) FinalizeStatement(statement); }
    }
    public void Execute(string sql, params string?[] values) => Query(sql, values);
    static void Check(int result) { if (result != 0) throw new IOException("SQLite statement failed."); }
    public void Dispose() { if (database != 0) { Close(database); database = 0; } }
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_open_v2", StringMarshalling=StringMarshalling.Utf8)] private static partial int Open(string path, out nint db, int flags, nint vfs);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_prepare_v2", StringMarshalling=StringMarshalling.Utf8)] private static partial int Prepare(nint db, string sql, int bytes, out nint statement, nint tail);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_bind_text", StringMarshalling=StringMarshalling.Utf8)] private static partial int BindText(nint statement, int index, string value, int bytes, nint destructor);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_bind_null")] private static partial int BindNull(nint statement, int index);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_busy_timeout")] private static partial int BusyTimeout(nint db, int milliseconds);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_step")] private static partial int Step(nint statement);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_column_count")] private static partial int ColumnCount(nint statement);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_column_text")] private static partial nint Text(nint statement, int column);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_finalize")] private static partial int FinalizeStatement(nint statement);
    [LibraryImport("libsqlite3.so.0", EntryPoint="sqlite3_close_v2")] private static partial int Close(nint db);
}
