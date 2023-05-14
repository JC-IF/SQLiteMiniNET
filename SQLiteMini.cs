#if NETSTANDARD2_1_OR_GREATER || NET47_OR_GREATER || NETCOREAPP3_0_OR_GREATER
// On older versions of .NET UTF-8 marshalling is not supported, this macro disables slower manual encoding conversions
#define UTF8_MARSHAL_SUPPORT
#endif

using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SQLiteMiniNET
{
    public class SQLiteMini : IDisposable
    {
        // Disable nullable warnings - using it wouldn't compile with C# < 8.0
#pragma warning disable CS8603
#pragma warning disable CS8604
#pragma warning disable CS8618
#pragma warning disable CS8625

        /**
         * QueryCtx represents an SQLite row along with info of the columns and a per query user-specified tag
         * It is created internally by Query function and it's passed to the callback handler
         * The handler uses this object to get field values from a SQLite row easily
         * Example:
         * var username = queryCtx.Get<string>("username");
         * var myOtherContextData = queryCtx.tag as MyType;
         */
        public class QueryCtx
        {
            public readonly string[] columns;
            public readonly object[] values;
            public readonly object tag;

            internal QueryCtx(string[] columns, object tag)
            {
                this.columns = columns;
                this.values = new object[columns.Length];
                this.tag = tag;
            }

            /**
             * It returns null only when a TEXT T<string> or BLOB T<byte[]> field is null
             * Throws an exception when the column was not found or the value type is different than requested
             * Using T<int> on a too big integer can throw OverflowException, use long for non-bool or enum integers
             * Using T<bool> on a value which is not 0 or 1 will throw an exception
             * 
             * Supported Types for T:
             *  bool => SQLITE_INTEGER (0, 1)
             *  int => SQLITE_INTEGER (-2147483648, 2147483647)
             *  long => SQLITE_INTEGER (all 64 bits)
             *  double => SQLITE_FLOAT
             *  string => SQLITE_TEXT | SQLITE_NULL
             *  byte[] => SQLITE_BLOB | SQLITE_NULL
             */
            public T Get<T>(string columnName)
            {
                int index = -1;
                for (int i = 0; i < columns.Length; i++)
                {
                    if (columns[i] == columnName)
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    throw new ArgumentException($"Column '{columnName}' not found");

                var _t = typeof(T);
                object val = this.values[index];

                if (val is null)
                    return default;

                if (val is T t)
                {
                    return t;
                }
                else if (val is long i64)
                {
                    if (_t == typeof(int))
                        return (T)(object)Convert.ToInt32(i64);

                    if (_t == typeof(bool) && (i64 == 0 || i64 == 1))
                        return (T)(object)(i64 == 1);
                }
                
                throw new InvalidOperationException($"Invalid type for column, attempted to read '{_t.Name}' but it's a '{val.GetType().Name}'");
            }
        }

        /**
         * BindCtx holds the parameters to be bound to the prepared statement
         * Usage:
         * var bindings = db.CreateBindings(storeExpandedSql: true);
         * bindings.Bind(1, "John");
         * bindings.Bind(":age", 40);
         * ... pass bindings to Query or Exec
         */
        public class BindCtx
        {
            public string ExpandedSQL { get; private set; }

            private readonly List<object> keys;
            private readonly List<object> values;
            private readonly bool expand_sql;

            internal BindCtx(bool storeExpandedSql)
            {
                this.keys = new List<object>();
                this.values = new List<object>();
                this.expand_sql = storeExpandedSql;
                ExpandedSQL = string.Empty;
            }

            public void Reset()
            {
                keys.Clear();
                values.Clear();
            }

            // Bind param by index (eg. ? placeholders, start from 1)
            public void Bind(int param_index, object data) => BindInternal(param_index, data);

            // Bind param by name (eg. :label placeholders, repeat : symbol also in this parameter)
            public void Bind(string param_name, object data) => BindInternal(param_name, data);

            private void BindInternal<T>(T k, object v)
            {
                if (k == null)
                    throw new ArgumentNullException("Can't bind null key");

                keys.Add(k);
                values.Add(v);
            }

            // Called from Query or Exec to bind all parameters to a prepared statement
            internal void Apply(IntPtr stmt)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    int param_index, s;
                    if (keys[i] is string param_name)
                        param_index = sqlite_bind_parameter_index(stmt, param_name);
                    else if (keys[i] is int j)
                        param_index = j;
                    else
                        throw new InvalidOperationException("Unexpected bind key type");

                    if (param_index < 1)
                        throw new SQLiteException("Bind parameter name was not found");

                    var val = values[i];
                    if (val is null)
                    {
                        s = sqlite3_bind_null(stmt, param_index);
                        AssertSqlite("sqlite3_bind_null", s);
                        continue;
                    }

                    var valueType = val.GetType();

                    if (valueType == typeof(string))
                    {
                        s = sqlite_bind_text(stmt, param_index, (string)val);
                        AssertSqlite("sqlite3_bind_text", s);
                    }
                    else if (valueType == typeof(byte[]))
                    {
                        // May consume less memory but requires unsafe keyword and SQLITE_STATIC control flow
                        s = sqlite_bind_blob(stmt, param_index, (byte[])val);
                        AssertSqlite("sqlite3_bind_blob", s);
                    }
                    else if (valueType == typeof(int) || valueType == typeof(long))
                    {
                        var i64val = Convert.ToInt64(val);
                        s = sqlite3_bind_int64(stmt, param_index, i64val);
                        AssertSqlite("sqlite3_bind_int64", s);
                    }
                    else if (valueType == typeof(double))
                    {
                        var doubleval = (double)val;
                        s = sqlite3_bind_double(stmt, param_index, doubleval);
                        AssertSqlite("sqlite3_bind_double", s);
                    }
                    else
                        throw new NotSupportedException($"Objects of type '{valueType.Name}' are not supported to be directly stored by SQLite format");
                }

                if (expand_sql)
                {
                    var pSql = sqlite3_expanded_sql(stmt);
                    AssertPointer("sqlite3_expanded_sql", pSql);
                    ExpandedSQL = ReadStringPtr(pSql);
                }
            }
        }

        public delegate bool QueryDelegate(QueryCtx row);
        private delegate object SqliteTypeMarshaler(IntPtr stmt, int n);

        public string FileName { get; }

        private IntPtr hDatabase;

        private static readonly Dictionary<int, SqliteTypeMarshaler> TypeMarshal;

        static SQLiteMini()
        {
            TypeMarshal = new Dictionary<int, SqliteTypeMarshaler>()
            {
                { SQLITE_INT, GetFieldInteger },
                { SQLITE_FLOAT, GetFieldDouble },
                { SQLITE_TEXT, GetFieldText },
                { SQLITE_BLOB, GetFieldBlob },
                { SQLITE_NULL, GetFieldNull }
            };
        }

        public SQLiteMini(string dbFileName, SQLiteOpenFlags mode)
        {
            FileName = dbFileName;
            hDatabase = IntPtr.Zero;
            int s = sqlite_open(FileName, out hDatabase, (int)mode);
            AssertSqlite("sqlite3_open_v2", s);
        }

        public BindCtx CreateBindings(bool storeExpandedSql = false) => new BindCtx(storeExpandedSql);

        public void Exec(string sql, BindCtx bindings = null)
        {
            AssertConnection();
            int s = sqlite_prepare(hDatabase, sql, -1, out IntPtr stmt);
            AssertSqlite("sqlite3_prepare_v2", s);
            try
            {
                bindings?.Apply(stmt);
                s = sqlite3_step(stmt);
                if (s != SQLITE_DONE)
                    throw new SQLiteException($"Unexpected result for sqlite3_step using exec: {s}", s);
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        public void Query(string sql, QueryDelegate handler, BindCtx bindings = null, object tag = null)
        {
            if (sql is null || handler is null)
                throw new ArgumentNullException("Required arguments for Query are null");

            AssertConnection();
            int s = sqlite_prepare(hDatabase, sql, -1, out IntPtr stmt);
            AssertSqlite("sqlite3_prepare_v2", s);
            try
            {
                bindings?.Apply(stmt);

                int n, col_count = sqlite3_column_count(stmt);
                // Check for empty result
                if (col_count < 1)
                    return;

                var columns = new string[col_count];
                var types = new int[col_count];
                for (n = 0; n < col_count; n++)
                {
                    IntPtr pName = sqlite3_column_name(stmt, n);
                    AssertPointer("sqlite3_column_name", pName);
                    columns[n] = ReadStringPtr(pName);
                }
                bool first = true;
                var ctx = new QueryCtx(columns, tag);
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    for (n = 0; n < col_count; n++)
                    {
                        if (first)
                            types[n] = sqlite3_column_type(stmt, n);

                        ctx.values[n] = TypeMarshal[types[n]].Invoke(stmt, n);
                    }

                    bool ok = handler(ctx);
                    if (!ok)
                        break;

                    first = false;
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        void IDisposable.Dispose() => this.Close();
        public void Close()
        {
            if (hDatabase != IntPtr.Zero)
            {
                sqlite3_close_v2(hDatabase);
                hDatabase = IntPtr.Zero;
            }
        }

        private void AssertConnection()
        {
            if (hDatabase == IntPtr.Zero)
                throw new InvalidOperationException("Can't use a closed database");
        }

        private static object GetFieldInteger(IntPtr stmt, int n)
            => sqlite3_column_int64(stmt, n);

        private static object GetFieldDouble(IntPtr stmt, int n)
            => sqlite3_column_double(stmt, n);

        private static object GetFieldText(IntPtr stmt, int n)
        {
            IntPtr pText = sqlite3_column_text(stmt, n);
            AssertPointer("sqlite3_column_text", pText);
            return ReadStringPtr(pText);
        }

        private static object GetFieldBlob(IntPtr stmt, int n)
        {
            int blob_size = sqlite3_column_bytes(stmt, n);
            if (blob_size <= 0)
                return new byte[0];

            IntPtr pBlob = sqlite3_column_blob(stmt, n);
            AssertPointer("sqlite3_column_blob", pBlob);
            var data = new byte[blob_size];
            Marshal.Copy(pBlob, data, 0, blob_size);
            return data;
        }

        private static object GetFieldNull(IntPtr stmt, int n) => null;


        private static void AssertSqlite(string name, int returnCode)
        {
            if (returnCode != SQLITE_OK)
                throw new SQLiteException($"SQLite native function '{name}' returned {returnCode}", returnCode);
        }

        private static void AssertPointer(string name, IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                throw new SQLiteException($"SQLite native function '{name}' returned NULL", 1);
        }

        [DllImport("sqlite3")] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("sqlite3")] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("sqlite3")] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("sqlite3")] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("sqlite3")] static extern IntPtr sqlite3_column_name(IntPtr stmt, int N); // returned utf-8 string is freed by finalize or next step
        [DllImport("sqlite3")] static extern int sqlite3_column_type(IntPtr stmt, int N);
        [DllImport("sqlite3")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int N);
        [DllImport("sqlite3")] static extern int sqlite3_column_bytes(IntPtr stmt, int N); // get size of BLOB value
        [DllImport("sqlite3")] static extern IntPtr sqlite3_column_text(IntPtr stmt, int N);
        [DllImport("sqlite3")] static extern double sqlite3_column_double(IntPtr stmt, int N);
        [DllImport("sqlite3")] static extern long sqlite3_column_int64(IntPtr stmt, int N);
        [DllImport("sqlite3")] static extern int sqlite3_bind_null(IntPtr stmt, int i);
        [DllImport("sqlite3")] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long data);
        [DllImport("sqlite3")] static extern int sqlite3_bind_double(IntPtr stmt, int i, double data);
        [DllImport("sqlite3")] static extern int sqlite3_bind_blob(IntPtr stmt, int i, IntPtr data, int size, IntPtr destructor); // 0 to free the buffer only after stmt finalize, SQLITE_TRANSIENT to enforce copy before return
        [DllImport("sqlite3")] static extern IntPtr sqlite3_expanded_sql(IntPtr stmt);

#pragma warning disable IDE1006 // Naming Styles

#if UTF8_MARSHAL_SUPPORT
        [DllImport("sqlite3")] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string dbFileName, out IntPtr ppDb, int flags, IntPtr zVfs);
        [DllImport("sqlite3")] static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int sql_maxlen, out IntPtr ppStmt, out IntPtr ppTail);
        [DllImport("sqlite3")] static extern int sqlite3_bind_text(IntPtr stmt, int i, [MarshalAs(UnmanagedType.LPUTF8Str)] string data, int size, IntPtr destructor);
        [DllImport("sqlite3")] static extern int sqlite3_bind_parameter_index(IntPtr stmt, [MarshalAs(UnmanagedType.LPUTF8Str)] string parameter_name);

        private static string ReadStringPtr(IntPtr ptr) => Marshal.PtrToStringUTF8(ptr);
        private static int sqlite_open(string dbFileName, out IntPtr ppDb, int flags) => sqlite3_open_v2(dbFileName, out ppDb, flags, IntPtr.Zero);
        private static int sqlite_prepare(IntPtr db, string sql, int sql_maxlen, out IntPtr ppStmt) => sqlite3_prepare_v2(db, sql, sql_maxlen, out ppStmt, out _);
        private static int sqlite_bind_text(IntPtr stmt, int i, string data) => sqlite3_bind_text(stmt, i, data, -1, SQLITE_TRANSIENT);
        private static int sqlite_bind_parameter_index(IntPtr stmt, string parameter_name) => sqlite3_bind_parameter_index(stmt, parameter_name);
#else
        [DllImport("sqlite3")] static extern int sqlite3_open_v2(IntPtr utf8_filename, out IntPtr ppDb, int flags, IntPtr zVfs);
        [DllImport("sqlite3")] static extern int sqlite3_prepare_v2(IntPtr db, IntPtr utf8_sql, int sql_maxlen, out IntPtr ppStmt, out IntPtr ppTail);
        [DllImport("sqlite3")] static extern int sqlite3_bind_text(IntPtr stmt, int i, IntPtr utf8_string, int size, IntPtr destructor);
        [DllImport("sqlite3")] static extern int sqlite3_bind_parameter_index(IntPtr stmt, IntPtr utf8_parameter_name);

        private static int sqlite_open(string dbFileName, out IntPtr ppDb, int flags)
        {
            using (var utf8str = UnmanagedBuffer.FromString(dbFileName))
                return sqlite3_open_v2(utf8str.Pointer, out ppDb, flags, IntPtr.Zero);
        }

        private static int sqlite_prepare(IntPtr db, string sql, int sql_maxlen, out IntPtr ppStmt)
        {
            using (var utf8sql = UnmanagedBuffer.FromString(sql))
                return sqlite3_prepare_v2(db, utf8sql.Pointer, utf8sql.Size - 1, out ppStmt, out _);
        }

        private static int sqlite_bind_text(IntPtr stmt, int i, string data)
        {
            using (var utf8str = UnmanagedBuffer.FromString(data))
                return sqlite3_bind_text(stmt, i, utf8str.Pointer, utf8str.Size - 1, SQLITE_TRANSIENT);
        }

        private static int sqlite_bind_parameter_index(IntPtr stmt, string parameter_name)
        {
            using (var utf8str = UnmanagedBuffer.FromString(parameter_name))
                return sqlite3_bind_parameter_index(stmt, utf8str.Pointer);
        }

        private static string ReadStringPtr(IntPtr ptr)
        {
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0)
                ++len;

            var utf8str = new byte[len];
            Marshal.Copy(ptr, utf8str, 0, len);
            return Encoding.UTF8.GetString(utf8str);
        }
#endif

#pragma warning disable IDE0063 // Use simple 'using' statement
        private static int sqlite_bind_blob(IntPtr stmt, int i, byte[] data)
        {
            using (var blob = new UnmanagedBuffer(data))
                return sqlite3_bind_blob(stmt, i, blob.Pointer, blob.Size, SQLITE_TRANSIENT);
        }
#pragma warning restore IDE0063 // Use simple 'using' statement
#pragma warning restore IDE1006 // Naming Styles

        // Used for simplifying the allocation/disposal of unmanaged memory
        internal class UnmanagedBuffer : IDisposable
        {
            public IntPtr Pointer { get; private set; }
            public readonly int Size;

            // Allocate buff in unmanaged memory
            public UnmanagedBuffer(byte[] buff)
            {
                Size = buff.Length;
                Pointer = Marshal.AllocHGlobal(Size);
                Marshal.Copy(buff, 0, Pointer, Size);
            }

            // Converts a .NET string to an unmanaged UTF-8 string
            public static UnmanagedBuffer FromString(string s)
            {
                var utf8str = Encoding.UTF8.GetBytes(s);
                var utf8nul = new byte[utf8str.Length + 1];
                Buffer.BlockCopy(utf8str, 0, utf8nul, 0, utf8str.Length);
                return new UnmanagedBuffer(utf8nul);
            }

            public void Free()
            {
                if (Pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Pointer);
                    Pointer = IntPtr.Zero;
                }
            }

            void IDisposable.Dispose() => Free();
        }

        private static readonly int SQLITE_OK = 0;
        private static readonly int SQLITE_ROW = 100;
        private static readonly int SQLITE_DONE = 101;
        private static readonly int SQLITE_INT = 1;
        private static readonly int SQLITE_FLOAT = 2;
        private static readonly int SQLITE_TEXT = 3;
        private static readonly int SQLITE_BLOB = 4;
        private static readonly int SQLITE_NULL = 5;
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);
    }
    // Re-enable nullable warnings
#pragma warning restore CS8603
#pragma warning restore CS8604
#pragma warning restore CS8618
#pragma warning restore CS8625

    [Flags]
    public enum SQLiteOpenFlags
    {
        ReadOnly = 0x01,
        ReadWrite = 0x02,
        Create = 0x04,
        URI = 0x40,
        InMemory = 0x80,
        MutexNone = 0x00008000,
        MutexFull = 0x00010000,
        CacheShared = 0x00020000,
        CachePrivate = 0x00040000,
        DontFollowLinks = 0x01000000
    }

    public class SQLiteException : Exception
    {
        public int ErrorCode { get; }

        public SQLiteException(string message, int native_error_code = 0) : base(message)
        {
            ErrorCode = native_error_code;
        }
    }
}