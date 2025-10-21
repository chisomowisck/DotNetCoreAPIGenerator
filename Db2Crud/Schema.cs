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
        // NEW: EF entity name used for C# type/DTO/service/filename (singular)
        public string EntityName { get; set; } = default!;

        // Physical table/view name in DB (may be plural)
        public string Name { get; set; } = default!;
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
        // Back-compat overload (entity assumed == table hint, no schema hint)
        public static List<TableInfo> Load(string provider, string conn, IEnumerable<string> tables)
            => Load(provider, conn, tables.Select(t => (Entity: t, Schema: "", Table: t)));

        // NEW: preferred overload – pass (EntityName, SchemaHint, TableHint)
        public static List<TableInfo> Load(
            string provider,
            string conn,
            IEnumerable<(string Entity, string Schema, string Table)> targets)
        {
            if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return SqlServer.Load(conn, targets);
            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return Postgres.Load(conn, targets);
            if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase)) return MySql.Load(conn, targets);
            throw new NotSupportedException($"Provider {provider}");
        }

        // ---------------- SQL Server ----------------
        internal static class SqlServer
        {
            // CHANGED: now takes (Entity, SchemaHint, TableHint)
            public static List<TableInfo> Load(
                string conn,
                IEnumerable<(string Entity, string Schema, string Table)> targets)
            {
                using var cn = new SqlConnection(conn);
                cn.Open();

                var list = new List<TableInfo>();

                // Cache all available tables/views once
                var availableTables = GetAvailableTables(cn);

                foreach (var (entityName, schemaHint, tableHint) in targets)
                {
                    var actual = FindActualTable(availableTables, TrimBrackets(schemaHint), TrimBrackets(tableHint));
                    if (actual == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[warn] Could not resolve physical table for entity '{entityName}' (hint: {schemaHint}.{tableHint}). Skipping.");
                        Console.ResetColor();
                        continue;
                    }

                    var ti = new TableInfo
                    {
                        EntityName = string.IsNullOrWhiteSpace(entityName) ? actual.Name : entityName,
                        Name       = actual.Name,
                        Schema     = actual.Schema,
                        IsView     = actual.IsView
                    };

                    ti.KeyColumn = GetPrimaryKey(cn, ti.Schema!, ti.Name);
                    ti.Columns.AddRange(GetColumns(cn, ti.Schema!, ti.Name));

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"[{ti.Schema}.{ti.Name}] => {ti.Columns.Count} columns, PK={ti.KeyColumn ?? "(none)"} view={ti.IsView} → Entity={ti.EntityName}");
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
                        Name   = r.GetString(1),
                        IsView = r.GetInt32(2) == 1
                    });
                }

                return tables;
            }

            // Try to resolve actual physical table from hints (supports plural/singular variations)
            private static TableInfo? FindActualTable(List<TableInfo> availableTables, string schema, string table)
            {
                // helper comparisons (null-safe)
                static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
                static bool EqI(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                bool SchemaOk(TableInfo t) => string.IsNullOrEmpty(schema) || EqI(t.Schema, schema);

                // 1) exact (case-sensitive) match
                var exact = availableTables.FirstOrDefault(t => Eq(t.Name, table) && SchemaOk(t));
                if (exact != null) return exact;

                // 2) case-insensitive match
                var ci = availableTables.FirstOrDefault(t => EqI(t.Name, table) && SchemaOk(t));
                if (ci != null) return ci;

                // 3) try singular/plural variations
                foreach (var v in GetTableNameVariations(table))
                {
                    var m = availableTables.FirstOrDefault(t => EqI(t.Name, v) && SchemaOk(t));
                    if (m != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.WriteLine($"[info] Found table using name variation: '{table}' -> '{m.Name}'");
                        Console.ResetColor();
                        return m;
                    }
                }

                // 4) if no schema provided, try all matches of name/variations; prefer exact, then base tables
                if (string.IsNullOrEmpty(schema))
                {
                    var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };
                    foreach (var v in GetTableNameVariations(table)) allNames.Add(v);

                    var matches = availableTables.Where(t => allNames.Contains(t.Name)).ToList();
                    if (matches.Count == 1) return matches[0];

                    if (matches.Count > 1)
                    {
                        var exactName = matches.FirstOrDefault(t => EqI(t.Name, table));
                        if (exactName != null) return exactName;

                        var preferTable = matches.FirstOrDefault(t => !t.IsView);
                        if (preferTable != null) return preferTable;

                        return matches[0];
                    }
                }

                return null;
            }

            private static List<string> GetTableNameVariations(string tableName)
            {
                var variations = new List<string>();
                if (tableName.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                {
                    variations.Add(tableName[..^1]);                   // -s
                    if (tableName.EndsWith("es", StringComparison.OrdinalIgnoreCase))
                        variations.Add(tableName[..^2]);               // -es
                    if (tableName.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && tableName.Length > 3)
                        variations.Add(tableName[..^3] + "y");        // -ies -> y
                }
                else
                {
                    variations.Add(tableName + "s");
                    if (tableName.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("z", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                        tableName.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
                        variations.Add(tableName + "es");
                    if (tableName.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
                        tableName.Length > 1 && !"aeiouAEIOU".Contains(tableName[^2]))
                        variations.Add(tableName[..^1] + "ies");
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
  AND TABLE_NAME   = @table", cn);

                    cmd.Parameters.AddWithValue("@schema", schema);
                    cmd.Parameters.AddWithValue("@table", table);

                    return cmd.ExecuteScalar() as string;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[warn] Error getting PK for [{schema}.{table}]: {ex.Message}");
                    Console.ResetColor();
                    return null;
                }
            }

            private static List<ColumnInfo> GetColumns(SqlConnection cn, string schema, string table)
            {
                var columns = new List<ColumnInfo>();

                // 1) INFORMATION_SCHEMA.COLUMNS
                try
                {
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
                        var isNullStr = r.GetString(1);
                        var sqlType = r.GetString(2);
                        var maxLength = r.IsDBNull(3) ? (int?)null : r.GetInt32(3);
                        var precision = r.IsDBNull(4) ? (int?)null : r.GetByte(4);
                        var scale = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);

                        columns.Add(new ColumnInfo
                        {
                            Name       = colName,
                            IsNullable = isNullStr.Equals("YES", StringComparison.OrdinalIgnoreCase),
                            ClrType    = MapSqlServerType(sqlType, isNullStr.Equals("YES", StringComparison.OrdinalIgnoreCase), maxLength, precision, scale),
                            CsName     = ToCsIdentifier(colName)
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[warn] Error via INFORMATION_SCHEMA for [{schema}.{table}]: {ex.Message}");
                    Console.ResetColor();
                }

                // 2) Fallback: sys.columns
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
JOIN sys.types t   ON t.user_type_id = c.user_type_id
JOIN sys.tables tb ON tb.object_id    = c.object_id
JOIN sys.schemas s ON s.schema_id     = tb.schema_id
WHERE s.name = @schema AND tb.name = @table
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
                                Name       = colName,
                                IsNullable = isNull,
                                ClrType    = MapSqlServerType(sqlType, isNull, maxLength, precision, scale),
                                CsName     = ToCsIdentifier(colName)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[warn] Error via sys.columns for [{schema}.{table}]: {ex.Message}");
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
                var type = sql.ToLowerInvariant();

                string core = type switch
                {
                    // numeric
                    "int" => "int",
                    "bigint" => "long",
                    "smallint" => "short",
                    "tinyint" => "byte",
                    "bit" => "bool",
                    "money" or "smallmoney" or "decimal" or "numeric" => "decimal",
                    "float" => "double",
                    "real" => "float",

                    // date/time
                    "date" => "DateOnly",                        // ✅ if your EF model uses DateOnly
                    "datetime" or "datetime2" or "smalldatetime" => "DateTime",
                    "datetimeoffset" => "DateTimeOffset",
                    "time" => "TimeSpan",

                    // unique / binary
                    "uniqueidentifier" => "Guid",
                    "varbinary" or "binary" or "image" or "timestamp" => "byte[]",

                    // strings
                    "varchar" or "nvarchar" or "nchar" or "char" or "text" or "ntext" or "xml" => "string",

                    _ => "string"
                };

                // Handle nullable
                if (core.EndsWith("[]")) return core; // arrays aren't nullable
                return nullable && core != "string" && core != "byte[]" ? core + "?" : core;
            }

           
        }

        // --------- Future providers (stubs) ----------
        internal static class Postgres
        {
            public static List<TableInfo> Load(string conn, IEnumerable<(string Entity, string Schema, string Table)> targets)
                => throw new NotImplementedException("Postgres schema reader not implemented yet.");
        }

        internal static class MySql
        {
            public static List<TableInfo> Load(string conn, IEnumerable<(string Entity, string Schema, string Table)> targets)
                => throw new NotImplementedException("MySql schema reader not implemented yet.");
        }
    }
}
