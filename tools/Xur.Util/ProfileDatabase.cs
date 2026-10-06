using Xur.IO;

namespace Xur.Util;

/// <summary>Read existing profiles without a managed SQLite dependency or schema changes.</summary>
public static class ProfileDatabase
{
    public static IEnumerable<(string Kind, string Json)> Documents(string path)
    {
        using var database = new NativeSqlite(path, readOnly: true);
        foreach (var row in database.Query("SELECT kind,json FROM documents WHERE kind IN ('profile','active','journal')"))
            yield return (row[0]!, row[1]!);
    }
}
