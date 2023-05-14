# SQLiteMini

SQLiteMini is a minimalist .NET cross-platform wrapper around the SQLite3 C library.

All the source code is a single and small C# file with no additional DLLs, no compiler unsafe flag required, compatible with all modern .NET versions and all operating systems supported by both sqlite and .NET runtime.
You can include it directly in your project or also build as a separate DLL (~10KB).

Supported Features:
- Open or create SQLite3 database by filename, uri or in memory, support for all standard flags (`SQLiteOpenFlags` enum)
- Parameters binding (support of all placeholders such as **?** or **:label** with easy API using `BindCtx` class)
- Execute SQL command (such as *INSERT*, *UPDATE*, *DELETE*... throws exception on failure)
- Execute SQL query with callback to read *SELECT* results in a memory-friendly way (using `QueryCtx` class)

## .NET Version support

Minimum runtime: **.NET Framework 2.0**

Faster UTF-8 string marshaling:
- NET Standard 2.1
- NET Core 3.0 or above
- NET Framework 4.7 or above

## Usage example

```
using System;
using SQLiteMiniNET;

// ...

using (var db = new SQLiteMini("citydb_file_name.sqlite", SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite))
{
    var createQuery = "CREATE TABLE cities (id INTEGER, name TEXT, population INTEGER, latitude REAL, longitude REAL, image BLOB, PRIMARY KEY(id AUTOINCREMENT));";
    db.Exec(createQuery);

    var insertQuery = "INSERT INTO cities (name, population, latitude, longitude, image) VALUES (:name, :population, :latitude, :longitude);";
    var bindings = db.CreateBindings(storeExpandedSql: true);
    bindings.Bind(":name", "Rome");
    bindings.Bind(":population", 4300000);
    bindings.Bind(":latitude", 41.893333);
    bindings.Bind(":longitude", 12.482778);
    //bindings.Bind(":image", File.ReadAllBytes("rome_thumbnail.jpg"));
    db.Exec(insertQuery, bindings);

    Console.WriteLine("OK. Executed SQL: " + bindings.ExpandedSQL);
    bindings.Reset();

    var selectQuery = "SELECT * FROM cities WHERE population > ?;";
    bindings.Bind(1, 123456);
    db.Query(
        sql: selectQuery,
        bindings: bindings,
        tag: null,
        handler: (row) =>
        {
            var name = row.Get<string>("name");
            var latitude = row.Get<double>("latitude");
            var longitude = row.Get<double>("longitude");
            var photo = row.Get<byte[]>("image_blob");

            // Cast and use row.tag object or use captured variable to store the information for later use
            Console.WriteLine("Found city: " + name);

            return true; // return false to interrupt the query (no further rows processing is needed)
        }
    );
    Console.ReadLine();
}
```

WON'T support features: sqlite3_backup_\* functions, VFS API, async pattern, ADO.NET LINQ integration
