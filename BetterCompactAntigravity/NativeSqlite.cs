using System.Runtime.InteropServices;

namespace BetterCompactAntigravity;

/// <summary>
/// Opens Antigravity SQLite databases read-only through the SQLite library bundled with Windows.
/// </summary>
sealed class NativeSqlite : IDisposable
{
    const int OpenReadOnlyFlag = 0x00000001;
    const int ResultRow = 100;
    const int ResultDone = 101;
    const int SqliteTransient = -1;

    readonly IntPtr database;
    bool disposed;

    NativeSqlite(IntPtr database) => this.database = database;

    /// <summary>Opens a database without modifying it.</summary>
    public static NativeSqlite OpenReadOnly(string path)
    {
        int result = SqliteOpen(path, out IntPtr database, OpenReadOnlyFlag, IntPtr.Zero);
        if (result != 0)
        {
            string error = database == IntPtr.Zero ? "SQLite veritabanı açılamadı." : ErrorText(database);
            if (database != IntPtr.Zero) SqliteClose(database);
            throw new IOException($"Veritabanı açılamadı ({path}): {error}");
        }
        return new NativeSqlite(database);
    }

    /// <summary>Prepares a query and binds optional UTF-8 text parameters in order.</summary>
    public SqliteStatement Prepare(string sql, params string[] parameters)
    {
        ThrowIfDisposed();
        int result = SqlitePrepare(database, sql, -1, out IntPtr statement, IntPtr.Zero);
        if (result != 0) throw new InvalidDataException(ErrorText(database));

        for (int i = 0; i < parameters.Length; i++)
        {
            result = SqliteBindText(statement, i + 1, parameters[i], -1, new IntPtr(SqliteTransient));
            if (result != 0)
            {
                SqliteFinalize(statement);
                throw new InvalidDataException(ErrorText(database));
            }
        }
        return new SqliteStatement(database, statement);
    }

    /// <summary>Closes the read-only connection.</summary>
    public void Dispose()
    {
        if (disposed) return;
        SqliteClose(database);
        disposed = true;
    }

    static string ErrorText(IntPtr database) => Marshal.PtrToStringUTF8(SqliteErrorMessage(database)) ?? "Bilinmeyen SQLite hatası.";

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(NativeSqlite));
    }

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int SqlitePrepare(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int SqliteBindText(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int byteCount, IntPtr destructor);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteStep(IntPtr statement);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SqliteColumnText(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SqliteColumnBlob(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteColumnBytes(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    static extern long SqliteColumnInt64(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteColumnType(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SqliteErrorMessage(IntPtr database);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteFinalize(IntPtr statement);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteClose(IntPtr database);

    /// <summary>Steps through one prepared query and reads columns safely.</summary>
    public sealed class SqliteStatement : IDisposable
    {
        readonly IntPtr database;
        readonly IntPtr statement;
        bool statementDisposed;

        internal SqliteStatement(IntPtr database, IntPtr statement)
        {
            this.database = database;
            this.statement = statement;
        }

        public bool Read()
        {
            if (statementDisposed) throw new ObjectDisposedException(nameof(SqliteStatement));
            int result = SqliteStep(statement);
            if (result == ResultRow) return true;
            if (result == ResultDone) return false;
            throw new InvalidDataException(ErrorText(database));
        }

        public string GetText(int column)
        {
            if (SqliteColumnType(statement, column) == 5) return string.Empty;
            IntPtr value = SqliteColumnText(statement, column);
            int length = SqliteColumnBytes(statement, column);
            return value == IntPtr.Zero || length == 0 ? string.Empty : Marshal.PtrToStringUTF8(value, length) ?? string.Empty;
        }

        public long GetInt64(int column) => SqliteColumnInt64(statement, column);

        public byte[] GetBlob(int column)
        {
            if (SqliteColumnType(statement, column) == 5) return [];
            int length = SqliteColumnBytes(statement, column);
            if (length <= 0) return [];
            byte[] value = new byte[length];
            Marshal.Copy(SqliteColumnBlob(statement, column), value, 0, length);
            return value;
        }

        public void Dispose()
        {
            if (statementDisposed) return;
            SqliteFinalize(statement);
            statementDisposed = true;
        }
    }
}
