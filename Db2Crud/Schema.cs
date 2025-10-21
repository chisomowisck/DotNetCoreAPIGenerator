#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Db2Crud
{
    public sealed class TableInfo
    {
        public string Name { get; set; } = default!;   // Table name (no brackets)
        public string? Schema { get; set; }            // e.g. dbo
        public string? KeyColumn { get; set; }
        public List<ColumnInfo> Columns { get; } = new();
        public bool IsView { get; set; }
    }

    public sealed class ColumnInfo
    {
        public string Name { get; set; } = default!;   // DB column name
        public string ClrType { get; set; } = "string";
        public bool IsNullable { get; set; }
        public string? CsName { get; set; }            // Safe C# identifier
    }

    public static class SchemaReader
    {
        public static List<TableInfo> Load(string provider, string conn, IEnumerable<string> tables)
        {
            if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return SqlServer.Load(conn, tables);
            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return Postgres.Load(conn, tables);
            if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase)) return MySql.Load(conn, tables);
            throw new NotSupportedException($"Provider {provider}");
        }

        // ---------------- SQL Server ----------------
        internal static class SqlServer
        {
            public static List<TableInfo> Load(string conn, IEnumerable<string> tables)
            {
                using var cn = new SqlConnection(conn);
                cn.Open();

                var list = new List<TableInfo>();

                // First, let's get all available tables/views to validate our input
                var availableTables = GetAvailableTables(cn);

                // Debug: show available tables
                if (availableTables.Count > 0)
                {
                    Console.WriteLine("Available tables in database:");
                    foreach (var table in availableTables.Take(20)) // Show first 20 to avoid spam
                    {
                        Console.WriteLine($"  {table.Schema}.{table.Name} ({(table.IsView ? "view" : "table")})");
                    }
                    if (availableTables.Count > 20)
                        Console.WriteLine($"  ... and {availableTables.Count - 20} more");
                }

                foreach (var raw in tables)
                {
                    // Accept "schema.Table", "[schema].[Table]" or just "Table"
                    ParseSchemaAndName(raw, out var schema, out var table);
                    schema = TrimBrackets(schema);
                    table = TrimBrackets(table);

                    // Try to find the actual table in the database
                    var actualTable = FindActualTable(availableTables, schema, table);
                    if (actualTable == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[warn] Table '{table}' with schema '{schema}' not found in database. Skipping.");
                        Console.ResetColor();
                        continue;
                    }

                    schema = actualTable.Schema;
                    table = actualTable.Name;

                    var ti = new TableInfo { Name = table, Schema = schema, IsView = actualTable.IsView };

                    // Get primary key
                    ti.KeyColumn = GetPrimaryKey(cn, schema, table);

                    // Get columns
                    ti.Columns.AddRange(GetColumns(cn, schema, table));

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"[{schema}.{table}] => {ti.Columns.Count} columns, PK={ti.KeyColumn ?? "(none)"} view={ti.IsView}");
                    Console.ResetColor();

                    list.Add(ti);
                }

                return list;
            }

            private static List<TableInfo> GetAvailableTables(SqlConnection cn)
            {
                var tables = new List<TableInfo>();

                using var cmd = new SqlCommand(@"
SELECT 
    TABLE_SCHEMA,
    TABLE_NAME,
    CASE WHEN TABLE_TYPE = 'VIEW' THEN 1 ELSE 0 END AS IsView
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
ORDER BY TABLE_SCHEMA, TABLE_NAME", cn);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    tables.Add(new TableInfo
                    {
                        Schema = r.GetString(0),
                        Name = r.GetString(1),
                        IsView = r.GetInt32(2) == 1
                    });
                }

                return tables;
            }

            private static TableInfo? FindActualTable(List<TableInfo> availableTables, string schema, string table)
            {
                // Exact match first (case-sensitive)
                var exactMatch = availableTables.FirstOrDefault(t =>
                    t.Name == table &&
                    (string.IsNullOrEmpty(schema) || t.Schema == schema));

                if (exactMatch != null)
                    return exactMatch;

                // Case-insensitive match
                var caseInsensitiveMatch = availableTables.FirstOrDefault(t =>
                    t.Name.Equals(table, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(schema) || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));

                if (caseInsensitiveMatch != null)
                    return caseInsensitiveMatch;

                // Try singular/plural variations
                var variations = GetTableNameVariations(table);
                foreach (var variation in variations)
                {
                    var variationMatch = availableTables.FirstOrDefault(t =>
                        t.Name.Equals(variation, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(schema) || t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));

                    if (variationMatch != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.WriteLine($"[info] Found table using name variation: '{table}' -> '{variationMatch.Name}'");
                        Console.ResetColor();
                        return variationMatch;
                    }
                }

                // If no schema specified, try to find any table with that name (or variations)
                if (string.IsNullOrEmpty(schema))
                {
                    var allVariations = new List<string> { table };
                    allVariations.AddRange(GetTableNameVariations(table));

                    var matches = availableTables.Where(t =>
                        allVariations.Any(v => t.Name.Equals(v, StringComparison.OrdinalIgnoreCase))).ToList();

                    if (matches.Count == 1)
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.WriteLine($"[info] Found table without schema: '{table}' -> '{matches[0].Schema}.{matches[0].Name}'");
                        Console.ResetColor();
                        return matches[0];
                    }
                    else if (matches.Count > 1)
                    {
                        // Prefer exact match if available
                        var exact = matches.FirstOrDefault(m => m.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
                        if (exact != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.WriteLine($"[info] Multiple matches for '{table}', using exact name match: '{exact.Schema}.{exact.Name}'");
                            Console.ResetColor();
                            return exact;
                        }

                        // Prefer tables over views
                        var tableMatch = matches.FirstOrDefault(m => !m.IsView);
                        if (tableMatch != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.WriteLine($"[info] Multiple matches for '{table}', using table (not view): '{tableMatch.Schema}.{tableMatch.Name}'");
                            Console.ResetColor();
                            return tableMatch;
                        }

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[warn] Multiple tables found for name '{table}': {string.Join(", ", matches.Select(m => m.Schema + "." + m.Name))}. Using first match: {matches[0].Schema}.{matches[0].Name}");
                        Console.ResetColor();
                        return matches[0];
                    }
                }

                return null;
            }

            private static List<string> GetTableNameVariations(string tableName)
            {
                var variations = new List<string>();

                // Common plural/singular variations
                if (tableName.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                {
                    // Try removing 's' for singular
                    variations.Add(tableName.Substring(0, tableName.Length - 1));

                    // Try removing 'es'
                    if (tableName.EndsWith("es", StringComparison.OrdinalIgnoreCase))
                    {
                        variations.Add(tableName.Substring(0, tableName.Length - 2));
                    }

                    // Try replacing 'ies' with 'y'
                    if (tableName.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && tableName.Length > 3)
                    {
                        variations.Add(tableName.Substring(0, tableName.Length - 3) + "y");
                    }
                }
                else
                {
                    // Try adding 's' for plural
                    variations.Add(tableName + "s");

                    // Try adding 'es' for words ending with s, x, z, ch, sh
                    if (tableName.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("z", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
                    {
                        variations.Add(tableName + "es");
                    }

                    // Try replacing 'y' with 'ies'
                    if (tableName.EndsWith("y", StringComparison.OrdinalIgnoreCase) && tableName.Length > 1 &&
                        !IsVowel(tableName[tableName.Length - 2]))
                    {
                        variations.Add(tableName.Substring(0, tableName.Length - 1) + "ies");
                    }
                }

                return variations.Distinct().ToList();
            }

            private static string? GetPrimaryKey(SqlConnection cn, string schema, string table)
            {
                try
                {
                    using var cmd = new SqlCommand(@"
SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
WHERE OBJECTPROPERTY(OBJECT_ID(CONSTRAINT_SCHEMA + '.' + CONSTRAINT_NAME), 'IsPrimaryKey') = 1
  AND TABLE_SCHEMA = @schema
  AND TABLE_NAME = @table", cn);

                    cmd.Parameters.AddWithValue("@schema", schema);
                    cmd.Parameters.AddWithValue("@table", table);

                    var result = cmd.ExecuteScalar();
                    return result as string;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[warn] Error getting primary key for [{schema}.{table}]: {ex.Message}");
                    Console.ResetColor();
                    return null;
                }
            }

            private static List<ColumnInfo> GetColumns(SqlConnection cn, string schema, string table)
            {
                var columns = new List<ColumnInfo>();

                try
                {
                    // First try INFORMATION_SCHEMA.COLUMNS (most reliable)
                    using var cmd = new SqlCommand(@"
SELECT 
    COLUMN_NAME,
    IS_NULLABLE,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH,
    NUMERIC_PRECISION,
    NUMERIC_SCALE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION", cn);

                    cmd.Parameters.AddWithValue("@schema", schema);
                    cmd.Parameters.AddWithValue("@table", table);

                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var colName = r.GetString(0);
                        var isNull = r.GetString(1).Equals("YES", StringComparison.OrdinalIgnoreCase);
                        var sqlType = r.GetString(2);
                        var maxLength = r.IsDBNull(3) ? (int?)null : r.GetInt32(3);
                        var precision = r.IsDBNull(4) ? (int?)null : r.GetByte(4);
                        var scale = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);

                        columns.Add(new ColumnInfo
                        {
                            Name = colName,
                            IsNullable = isNull,
                            ClrType = MapSqlServerType(sqlType, isNull, maxLength, precision, scale),
                            CsName = ToCsIdentifier(colName)
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[warn] Error getting columns via INFORMATION_SCHEMA for [{schema}.{table}]: {ex.Message}");
                    Console.ResetColor();
                }

                // Fallback to sys.columns if INFORMATION_SCHEMA returns no results
                if (columns.Count == 0)
                {
                    try
                    {
                        using var cmd = new SqlCommand(@"
SELECT 
    c.name AS COLUMN_NAME,
    c.is_nullable,
    t.name AS DATA_TYPE,
    c.max_length,
    c.precision,
    c.scale
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
JOIN sys.tables tab ON tab.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = tab.schema_id
WHERE s.name = @schema AND tab.name = @table
ORDER BY c.column_id", cn);

                        cmd.Parameters.AddWithValue("@schema", schema);
                        cmd.Parameters.AddWithValue("@table", table);

                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var colName = r.GetString(0);
                            var isNull = r.GetBoolean(1);
                            var sqlType = r.GetString(2);
                            var maxLength = r.GetInt16(3);
                            var precision = r.GetByte(4);
                            var scale = r.GetInt32(5);

                            columns.Add(new ColumnInfo
                            {
                                Name = colName,
                                IsNullable = isNull,
                                ClrType = MapSqlServerType(sqlType, isNull, maxLength, precision, scale),
                                CsName = ToCsIdentifier(colName)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[warn] Error getting columns via sys.columns for [{schema}.{table}]: {ex.Message}");
                        Console.ResetColor();
                    }
                }

                if (columns.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[warn] No columns found for [{schema}.{table}]");
                    Console.ResetColor();
                }

                return columns;
            }

            private static string TrimBrackets(string s)
                => string.IsNullOrWhiteSpace(s) ? s : s.Trim().TrimStart('[').TrimEnd(']');

            private static void ParseSchemaAndName(string raw, out string schema, out string table)
            {
                var cleaned = raw.Trim();
                cleaned = TrimBrackets(cleaned);

                var parts = cleaned.Split('.', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    schema = parts[0];
                    table = parts[1];
                }
                else
                {
                    schema = "";
                    table = cleaned;
                }
            }

            private static string ToCsIdentifier(string name)
            {
                var s = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
                if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;

                var keywords = new HashSet<string>(StringComparer.Ordinal)
                {
                    "class","namespace","public","private","protected","internal","static","void","int",
                    "string","long","short","bool","true","false","null","using","return","new","record",
                    "event","base","this","params","object","decimal","double","float"
                };
                if (keywords.Contains(s)) s += "_";
                return s;
            }

            private static string MapSqlServerType(string sql, bool nullable, int? maxLength = null, int? precision = null, int? scale = null)
            {
                string core = sql.ToLowerInvariant() switch
                {
                    "int" => "int",
                    "bigint" => "long",
                    "smallint" => "short",
                    "tinyint" => "byte",
                    "bit" => "bool",
                    "money" or "smallmoney" or "decimal" or "numeric" => "decimal",
                    "float" => "double",
                    "real" => "float",
                    "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
                    "datetimeoffset" => "DateTimeOffset",
                    "time" => "TimeSpan",
                    "uniqueidentifier" => "Guid",
                    "varbinary" or "binary" or "image" or "timestamp" => "byte[]",
                    "varchar" or "char" or "text" when maxLength == 1 => "char", // Single character
                    "varchar" or "nvarchar" or "nchar" or "char" or "text" or "ntext" or "xml" => "string",
                    _ => "string"
                };

                if (core.EndsWith("[]")) return core; // arrays aren't nullable
                return nullable && core != "string" && core != "byte[]" ? core + "?" : core;
            }

            private static bool IsVowel(char c)
            {
                return "aeiouAEIOU".IndexOf(c) >= 0;
            }
        }

        // --------- Future providers (stubs) ----------
        internal static class Postgres
        {
            public static List<TableInfo> Load(string conn, IEnumerable<string> tables)
                => throw new NotImplementedException("Postgres schema reader not implemented yet.");
        }

        internal static class MySql
        {
            public static List<TableInfo> Load(string conn, IEnumerable<string> tables)
                => throw new NotImplementedException("MySql schema reader not implemented yet.");
        }
    }
}

//#nullable enable
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text.RegularExpressions;
//using Microsoft.Data.SqlClient;

//namespace Db2Crud
//{
//    public sealed class TableInfo
//    {
//        public string Name { get; set; } = default!;   // Table name (no brackets)
//        public string? Schema { get; set; }            // e.g. dbo
//        public string? KeyColumn { get; set; }
//        public List<ColumnInfo> Columns { get; } = new();
//        public bool IsView { get; set; }
//    }

//    public sealed class ColumnInfo
//    {
//        public string Name { get; set; } = default!;   // DB column name
//        public string ClrType { get; set; } = "string";
//        public bool IsNullable { get; set; }
//        public string? CsName { get; set; }            // Safe C# identifier
//    }

//    public static class SchemaReader
//    {
//        public static List<TableInfo> Load(string provider, string conn, IEnumerable<string> tables)
//        {
//            if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return SqlServer.Load(conn, tables);
//            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return Postgres.Load(conn, tables);
//            if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase)) return MySql.Load(conn, tables);
//            throw new NotSupportedException($"Provider {provider}");
//        }

//        // ---------------- SQL Server ----------------
//        internal static class SqlServer
//        {
//            public static List<TableInfo> Load(string conn, IEnumerable<string> tables)
//            {
//                using var cn = new SqlConnection(conn);
//                cn.Open();

//                var list = new List<TableInfo>();

//                // resolve schema if missing (prefer dbo)
//                using var findSchemaCmd = new SqlCommand(@"
//SELECT TOP (1) TABLE_SCHEMA
//FROM INFORMATION_SCHEMA.TABLES
//WHERE TABLE_NAME = @t AND TABLE_TYPE IN ('BASE TABLE','VIEW')
//ORDER BY CASE WHEN TABLE_SCHEMA = 'dbo' THEN 0 ELSE 1 END;", cn);

//                // is view?
//                using var isViewCmd = new SqlCommand(@"
//SELECT CASE WHEN OBJECT_ID(QUOTENAME(@s)+'.'+QUOTENAME(@t),'V') IS NOT NULL THEN 1 ELSE 0 END;", cn);

//                // PK (single column) via sys.*
//                using var pkCmd = new SqlCommand(@"
//SELECT TOP (1) c.name
//FROM sys.key_constraints kc
//JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
//JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
//WHERE kc.parent_object_id = OBJECT_ID(QUOTENAME(@s)+'.'+QUOTENAME(@t))
//  AND kc.type = 'PK'
//ORDER BY ic.key_ordinal;", cn);

//                // columns via sys.*
//                using var colCmd = new SqlCommand(@"
//SELECT c.name AS COLUMN_NAME,
//       c.is_nullable,
//       t.name AS DATA_TYPE
//FROM sys.columns c
//JOIN sys.types t ON t.user_type_id = c.user_type_id
//WHERE c.object_id = OBJECT_ID(QUOTENAME(@s)+'.'+QUOTENAME(@t))
//ORDER BY c.column_id;", cn);


//                foreach (var raw in tables)
//                {
//                    // Accept "schema.Table", "[schema].[Table]" or just "Table"
//                    ParseSchemaAndName(raw, out var schema, out var table);
//                    schema = TrimBrackets(schema);
//                    table  = TrimBrackets(table);

//                    // 1) Try INFORMATION_SCHEMA to get schema
//                    string? resolvedSchema = null;
//                    if (string.IsNullOrWhiteSpace(schema))
//                    {
//                        findSchemaCmd.Parameters.Clear();
//                        findSchemaCmd.Parameters.AddWithValue("@t", table);
//                        resolvedSchema = findSchemaCmd.ExecuteScalar() as string;

//                        // 2) Fallback: search sys.objects across schemas
//                        if (resolvedSchema is null)
//                        {
//                            using var findSchemaSys = new SqlCommand(@"
//SELECT TOP (1) s.name
//FROM sys.objects o
//JOIN sys.schemas s ON s.schema_id = o.schema_id
//WHERE o.name = @t AND o.type IN ('U','V')   -- U=table, V=view
//ORDER BY CASE WHEN s.name='dbo' THEN 0 ELSE 1 END;", cn);
//                            findSchemaSys.Parameters.AddWithValue("@t", table);
//                            resolvedSchema = findSchemaSys.ExecuteScalar() as string;
//                        }

//                        // 3) Last resort
//                        schema = resolvedSchema ?? "dbo";
//                        if (resolvedSchema is null)
//                        {
//                            Console.ForegroundColor = ConsoleColor.Yellow;
//                            Console.WriteLine($"[warn] Could not resolve schema for table '{table}', defaulting to dbo.");
//                            Console.ResetColor();
//                        }
//                    }

//                    // Is view?
//                    isViewCmd.Parameters.Clear();
//                    isViewCmd.Parameters.AddWithValue("@s", schema);
//                    isViewCmd.Parameters.AddWithValue("@t", table);
//                    var isView = (int)isViewCmd.ExecuteScalar() == 1;

//                    var ti = new TableInfo { Name = table, Schema = schema, IsView = isView };

//                    // PK (single column)
//                    pkCmd.Parameters.Clear();
//                    pkCmd.Parameters.AddWithValue("@s", schema);
//                    pkCmd.Parameters.AddWithValue("@t", table);
//                    ti.KeyColumn = pkCmd.ExecuteScalar() as string;

//                    // Columns via sys.*
//                    colCmd.Parameters.Clear();
//                    colCmd.Parameters.AddWithValue("@s", schema);
//                    colCmd.Parameters.AddWithValue("@t", table);
//                    ti.Columns.Clear();
//                    using (var r = colCmd.ExecuteReader())
//                    {
//                        while (r.Read())
//                        {
//                            var colName = r.GetString(0);
//                            var isNull = r.GetBoolean(1);
//                            var sqlType = r.GetString(2);
//                            ti.Columns.Add(new ColumnInfo
//                            {
//                                Name       = colName,
//                                IsNullable = isNull,
//                                ClrType    = MapSqlServerType(sqlType, isNull),
//                                CsName     = ToCsIdentifier(colName)
//                            });
//                        }
//                    }

//                    // Fallback: if still zero columns, try INFORMATION_SCHEMA.COLUMNS as a safety net
//                    if (ti.Columns.Count == 0)
//                    {
//                        using var colFallback = new SqlCommand(@"
//SELECT COLUMN_NAME, IS_NULLABLE, DATA_TYPE
//FROM INFORMATION_SCHEMA.COLUMNS
//WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t
//ORDER BY ORDINAL_POSITION;", cn);
//                        colFallback.Parameters.AddWithValue("@s", schema);
//                        colFallback.Parameters.AddWithValue("@t", table);
//                        using var rf = colFallback.ExecuteReader();
//                        while (rf.Read())
//                        {
//                            var colName = rf.GetString(0);
//                            var isNull = rf.GetString(1).Equals("YES", StringComparison.OrdinalIgnoreCase);
//                            var sqlType = rf.GetString(2);
//                            ti.Columns.Add(new ColumnInfo
//                            {
//                                Name       = colName,
//                                IsNullable = isNull,
//                                ClrType    = MapSqlServerType(sqlType, isNull),
//                                CsName     = ToCsIdentifier(colName)
//                            });
//                        }

//                        if (ti.Columns.Count == 0)
//                        {
//                            Console.ForegroundColor = ConsoleColor.Yellow;
//                            Console.WriteLine($"[warn] No columns found for [{schema}.{table}] (OBJECT_ID null?). Check actual schema/table name.");
//                            Console.ResetColor();
//                        }
//                    }

//                    Console.ForegroundColor = ConsoleColor.DarkGray;
//                    Console.WriteLine($"[{schema}.{table}] => {ti.Columns.Count} columns, PK={ti.KeyColumn ?? "(none)"} view={ti.IsView}");
//                    Console.ResetColor();

//                    list.Add(ti);
//                }
//                    //foreach (var raw in tables)
//                    //{
//                    //    // Accept:
//                    //    //   "schema.Table"
//                    //    //   "[schema].[Table]"
//                    //    //   "Table"
//                    //    ParseSchemaAndName(raw, out var schema, out var table);
//                    //    schema = TrimBrackets(schema);
//                    //    table  = TrimBrackets(table);

//                    //    if (string.IsNullOrWhiteSpace(schema))
//                    //    {
//                    //        findSchemaCmd.Parameters.Clear();
//                    //        findSchemaCmd.Parameters.AddWithValue("@t", table);
//                    //        schema = (string?)findSchemaCmd.ExecuteScalar() ?? "dbo";
//                    //    }

//                    //    // view?
//                    //    isViewCmd.Parameters.Clear();
//                    //    isViewCmd.Parameters.AddWithValue("@s", schema);
//                    //    isViewCmd.Parameters.AddWithValue("@t", table);
//                    //    var isView = (int)isViewCmd.ExecuteScalar() == 1;

//                    //    var ti = new TableInfo { Name = table, Schema = schema, IsView = isView };

//                    //    // PK
//                    //    pkCmd.Parameters.Clear();
//                    //    pkCmd.Parameters.AddWithValue("@s", schema);
//                    //    pkCmd.Parameters.AddWithValue("@t", table);
//                    //    ti.KeyColumn = pkCmd.ExecuteScalar() as string;

//                    //    // columns
//                    //    colCmd.Parameters.Clear();
//                    //    colCmd.Parameters.AddWithValue("@s", schema);
//                    //    colCmd.Parameters.AddWithValue("@t", table);

//                    //    using (var r = colCmd.ExecuteReader())
//                    //    {
//                    //        while (r.Read())
//                    //        {
//                    //            var colName = r.GetString(0);
//                    //            var isNull = r.GetBoolean(1);
//                    //            var sqlType = r.GetString(2);

//                    //            ti.Columns.Add(new ColumnInfo
//                    //            {
//                    //                Name       = colName,
//                    //                IsNullable = isNull,
//                    //                ClrType    = MapSqlServerType(sqlType, isNull),
//                    //                CsName     = ToCsIdentifier(colName)
//                    //            });
//                    //        }
//                    //    }

//                    //    // (optional) quick visibility for debugging
//                    //    Console.ForegroundColor = ConsoleColor.DarkGray;
//                    //    Console.WriteLine($"[{schema}.{table}] => {ti.Columns.Count} columns, PK={ti.KeyColumn ?? "(none)"} view={ti.IsView}");
//                    //    Console.ResetColor();

//                    //    list.Add(ti);
//                    //}

//                    return list;
//                }

//            private static string TrimBrackets(string s)
//                => string.IsNullOrWhiteSpace(s) ? s : s.Trim().TrimStart('[').TrimEnd(']');

//            private static void ParseSchemaAndName(string raw, out string schema, out string table)
//            {
//                var cleaned = raw.Trim();
//                // strip surrounding brackets on the whole thing, too
//                cleaned = TrimBrackets(cleaned);

//                var parts = cleaned.Split('.', 2, StringSplitOptions.TrimEntries);
//                if (parts.Length == 2)
//                {
//                    schema = parts[0];
//                    table  = parts[1];
//                }
//                else
//                {
//                    schema = "";
//                    table  = cleaned;
//                }
//            }

//            private static string ToCsIdentifier(string name)
//            {
//                var s = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
//                if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;

//                var keywords = new HashSet<string>(StringComparer.Ordinal)
//                {
//                    "class","namespace","public","private","protected","internal","static","void","int",
//                    "string","long","short","bool","true","false","null","using","return","new","record",
//                    "event","base","this","params","object","decimal","double","float"
//                };
//                if (keywords.Contains(s)) s += "_";
//                return s;
//            }

//            private static string MapSqlServerType(string sql, bool nullable)
//            {
//                string core = sql switch
//                {
//                    "int" => "int",
//                    "bigint" => "long",
//                    "smallint" => "short",
//                    "tinyint" => "byte",
//                    "bit" => "bool",
//                    "money" or "smallmoney" or "decimal" or "numeric" => "decimal",
//                    "float" => "double",
//                    "real" => "float",
//                    "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
//                    "time" => "TimeSpan",
//                    "uniqueidentifier" => "Guid",
//                    "varbinary" or "binary" or "image" => "byte[]",
//                    // nvarchar, varchar, nchar, char, text, ntext, xml, etc.
//                    _ => "string"
//                };
//                if (core.EndsWith("[]")) return core; // arrays aren't nullable
//                return nullable && core != "string" ? core + "?" : core;
//            }
//        }

//        // --------- Future providers (stubs) ----------
//        internal static class Postgres
//        {
//            public static List<TableInfo> Load(string conn, IEnumerable<string> tables)
//                => throw new NotImplementedException("Postgres schema reader not implemented yet.");
//        }

//        internal static class MySql
//        {
//            public static List<TableInfo> Load(string conn, IEnumerable<string> tables)
//                => throw new NotImplementedException("MySql schema reader not implemented yet.");
//        }
//    }
//}
