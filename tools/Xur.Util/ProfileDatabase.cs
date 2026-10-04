using System.Runtime.InteropServices;

namespace Xur.Util;

/// <summary>Read existing profiles without a managed SQLite dependency or schema changes.</summary>
public static partial class ProfileDatabase
{
    [LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string filename, out nint database, int flags, nint vfs);
    [LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Prepare(nint database, string sql, int bytes, out nint statement, nint tail);
    [LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_step")]
    private static partial int Step(nint statement);
    [LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_column_text")]
    private static partial nint Text(nint statement, int column);
    [LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_finalize")]
    private static partial int FinalizeStatement(nint statement);
    [LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_close_v2")]
    private static partial int Close(nint database);

    public static IEnumerable<(string Kind, string Json)> Documents(string path)
    {
        var opened = Open(path, out var database, 1, 0); // SQLITE_OPEN_READONLY; never create or migrate state.
        try
        {
            if (opened != 0) throw new IOException("Could not open the profile database read-only");
            var prepared = Prepare(database, "SELECT kind,json FROM documents WHERE kind IN ('profile','active','journal')", -1, out var statement, 0);
            try
            {
                if (prepared != 0) throw new IOException("Could not inspect profile compatibility");
                int result;
                while ((result = Step(statement)) == 100)
                    yield return (Marshal.PtrToStringUTF8(Text(statement, 0))!, Marshal.PtrToStringUTF8(Text(statement, 1))!);
                if (result != 101) throw new IOException("Profile compatibility query failed");
            }
            finally { if (statement != 0) FinalizeStatement(statement); }
        }
        finally { if (database != 0) Close(database); }
    }
}
