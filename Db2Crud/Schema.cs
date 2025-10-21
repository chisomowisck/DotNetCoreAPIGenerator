#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient; // MSSQL client

namespace Db2Crud
{
    public sealed class TableInfo
    {
        public string Name { get; set; } = default!;              // Table name (no schema)
        public string? Schema { get; set; }                       // Table schema (e.g., dbo)
        public string? KeyColumn { get; set; }                    // single-key assumption; extend for composite keys
        public List<ColumnInfo> Columns { get; } = new();
        public bool IsView { get; set; }
    }

    public sealed class ColumnInfo
    {
        public string Name { get; set; } = default!;              // DB column name
        public string ClrType { get; set; } = "string";
        public bool IsNullable { get; set; }
        public string? CsName { get; set; }                       // Safe C# identifier (mapped from Name)
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

                using var cn = new Microsoft.Data.SqlClient.SqlConnection(conn);
                cn.Open();

                var list = new List<TableInfo>();

                // Resolve schema when not provided (prefer dbo)
                using var findSchemaCmd = new Microsoft.Data.SqlClient.SqlCommand(@"
            SELECT TOP (1) TABLE_SCHEMA
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @t AND TABLE_TYPE IN ('BASE TABLE','VIEW')
            ORDER BY CASE WHEN TABLE_SCHEMA = 'dbo' THEN 0 ELSE 1 END;", cn);

                // Is it a view?
                using var isViewCmd = new Microsoft.Data.SqlClient.SqlCommand(@"
            SELECT CASE WHEN EXISTS (
                SELECT 1 
                FROM sys.views v
                JOIN sys.schemas s ON s.schema_id = v.schema_id
                WHERE v.name = @t AND s.name = @s
            ) THEN 1 ELSE 0 END;", cn);

                // Primary key (single-column assumed)
                using var pkCmd = new Microsoft.Data.SqlClient.SqlCommand(@"
            SELECT TOP (1) c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c
              ON c.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
             AND c.TABLE_SCHEMA    = tc.TABLE_SCHEMA
             AND c.TABLE_NAME      = tc.TABLE_NAME
            WHERE tc.TABLE_SCHEMA = @s
              AND tc.TABLE_NAME   = @t
              AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY c.ORDINAL_POSITION;", cn);

                // Columns (filter by schema!)
                using var colCmd = new Microsoft.Data.SqlClient.SqlCommand(@"
            SELECT COLUMN_NAME, IS_NULLABLE, DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @s
              AND TABLE_NAME   = @t
            ORDER BY ORDINAL_POSITION;", cn);

                foreach (var raw in tables)
                {
                    // Accept "schema.Table" or just "Table"
                    string schema, table;
                    ParseSchemaAndName(raw, out schema, out table);

                    if (string.IsNullOrWhiteSpace(schema))
                    {
                        findSchemaCmd.Parameters.Clear();
                        findSchemaCmd.Parameters.AddWithValue("@t", table);
                        schema = (string?)findSchemaCmd.ExecuteScalar() ?? "dbo";
                    }

                    isViewCmd.Parameters.Clear();
                    isViewCmd.Parameters.AddWithValue("@s", schema);
                    isViewCmd.Parameters.AddWithValue("@t", table);
                    var isView = (int)isViewCmd.ExecuteScalar() == 1;

                    var ti = new TableInfo { Name = table, Schema = schema, IsView = isView };

                    // PK
                    pkCmd.Parameters.Clear();
                    pkCmd.Parameters.AddWithValue("@s", schema);
                    pkCmd.Parameters.AddWithValue("@t", table);
                    ti.KeyColumn = pkCmd.ExecuteScalar() as string;

                    // Columns
                    colCmd.Parameters.Clear();
                    colCmd.Parameters.AddWithValue("@s", schema);
                    colCmd.Parameters.AddWithValue("@t", table);
                    using (var r = colCmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var name = r.GetString(0);
                            var isNull = r.GetString(1) == "YES";
                            var sql = r.GetString(2);

                            ti.Columns.Add(new ColumnInfo
                            {
                                Name       = name,
                                IsNullable = isNull,
                                ClrType    = MapSqlServerType(sql, isNull),
                                CsName     = ToCsIdentifier(name)
                            });
                        }
                    }

                    list.Add(ti);
                }

                return list;

                //using var cn = new SqlConnection(conn);
                //cn.Open();

                //var list = new List<TableInfo>();

                //// Prepared commands reused inside the loop
                //using var findSchemaCmd = new SqlCommand(@"
                //    SELECT TOP (1) TABLE_SCHEMA
                //    FROM INFORMATION_SCHEMA.TABLES
                //    WHERE TABLE_NAME = @t
                //    ORDER BY CASE WHEN TABLE_SCHEMA = 'dbo' THEN 0 ELSE 1 END, TABLE_SCHEMA;", cn);

                //using var pkCmd = new SqlCommand(@"
                //    SELECT TOP (1) c.COLUMN_NAME
                //    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                //    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c
                //      ON c.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                //    WHERE tc.TABLE_NAME = @t
                //      AND tc.TABLE_SCHEMA = @s
                //      AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                //    ORDER BY c.ORDINAL_POSITION;", cn);

                //  using var colCmd = new SqlCommand(@"
                //    SELECT COLUMN_NAME, IS_NULLABLE, DATA_TYPE
                //    FROM INFORMATION_SCHEMA.COLUMNS
                //    WHERE TABLE_NAME = @t
                //      AND TABLE_SCHEMA = @s
                //    ORDER BY ORDINAL_POSITION;", cn);

                //using var isViewCmd = new SqlCommand(@"
                //    SELECT CASE WHEN EXISTS (
                //      SELECT 1
                //      FROM INFORMATION_SCHEMA.VIEWS
                //      WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t
                //    ) THEN 1 ELSE 0 END;", cn);

                //foreach (var raw in tables)
                //{
                //    // Accept "schema.Table" or just "Table"
                //    string schema, table;
                //    ParseSchemaAndName(raw, out schema, out table);

                //    // If no schema was provided, try to resolve it (prefer dbo)
                //    if (string.IsNullOrWhiteSpace(schema))
                //    {
                //        findSchemaCmd.Parameters.Clear();
                //        findSchemaCmd.Parameters.AddWithValue("@t", table);
                //        schema = (string?)findSchemaCmd.ExecuteScalar() ?? "dbo";
                //    }

                //    isViewCmd.Parameters.Clear();
                //    isViewCmd.Parameters.AddWithValue("@s", schema);
                //    isViewCmd.Parameters.AddWithValue("@t", table);
                //    var isView = (int)isViewCmd.ExecuteScalar() == 1;

                //    var ti = new TableInfo { Name = table, Schema = schema, IsView = isView };

                //    //var ti = new TableInfo { Name = table, Schema = schema };

                //    // PK (single column)
                //    pkCmd.Parameters.Clear();
                //    pkCmd.Parameters.AddWithValue("@t", table);
                //    pkCmd.Parameters.AddWithValue("@s", schema);
                //    ti.KeyColumn = pkCmd.ExecuteScalar() as string;

                //    // Columns
                //    colCmd.Parameters.Clear();
                //    colCmd.Parameters.AddWithValue("@t", table);
                //    colCmd.Parameters.AddWithValue("@s", schema);
                //    using (var r = colCmd.ExecuteReader())
                //    {
                //        while (r.Read())
                //        {
                //            var name = r.GetString(0);
                //            var isNull = r.GetString(1) == "YES";
                //            var sql = r.GetString(2);

                //            ti.Columns.Add(new ColumnInfo
                //            {
                //                Name      = name,
                //                IsNullable = isNull,
                //                ClrType   = MapSqlServerType(sql, isNull),
                //                CsName    = ToCsIdentifier(name)
                //            });
                //        }
                //    }

                //    list.Add(ti);
                //}

                //return list;
            }

            //private static void ParseSchemaAndName(string raw, out string schema, out string name)
            //{
            //    schema = "";
            //    name   = raw.Trim();

            //    // Accept formats like "dbo.Users" or "[dbo].[Users]"
            //    var m = Regex.Match(raw, @"^(?:\[(?<s>[^\]]+)\]|\b(?<s>[^.\s]+))\.(?:\[(?<n>[^\]]+)\]|\b(?<n>[^.\s]+))$");
            //    if (m.Success)
            //    {
            //        schema = m.Groups["s"].Value;
            //        name   = m.Groups["n"].Value;
            //    }
            //}

            private static void ParseSchemaAndName(string raw, out string schema, out string table)
            {
                var parts = raw.Split('.', 2);
                if (parts.Length == 2) { schema = parts[0]; table = parts[1]; }
                else { schema = ""; table = raw; }
            }

            private static string ToCsIdentifier(string name)
            {
                var s = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
                if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;

                // A small reserved keyword set
                var keywords = new HashSet<string>(StringComparer.Ordinal)
                {
                    "class","namespace","public","private","protected","internal","static","void","int",
                    "string","long","short","bool","true","false","null","using","return","new","record",
                    "event","base","this","params","object","decimal","double","float"
                };
                if (keywords.Contains(s)) s += "_";
                return s;
            }

            //private static string MapSqlServerType(string sql, bool nullable)
            //{
            //    string core = sql switch
            //    {
            //        "int" => "int",
            //        "bigint" => "long",
            //        "smallint" => "short",
            //        "tinyint" => "byte",
            //        "bit" => "bool",
            //        "money" or "smallmoney" or "decimal" or "numeric" => "decimal",
            //        "float" => "double",
            //        "real" => "float",
            //        "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
            //        "datetimeoffset" => "DateTimeOffset",
            //        "time" => "TimeSpan",
            //        "uniqueidentifier" => "Guid",
            //        "varbinary" or "binary" or "image" => "byte[]",
            //        // strings
            //        "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" => "string",
            //        // xml / sql_variant as string by default
            //        "xml" or "sql_variant" => "string",
            //        _ => "string"
            //    };

            //    if (core.EndsWith("[]")) return core; // arrays not nullable
            //    return nullable && core != "string" ? core + "?" : core;
            //}

            private static string MapSqlServerType(string sql, bool nullable)
            {
                string core = sql switch
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
                    "time" => "TimeSpan",
                    "uniqueidentifier" => "Guid",
                    "varbinary" or "binary" or "image" => "byte[]",
                    _ => "string"
                };
                if (core.EndsWith("[]")) return core;
                return nullable && core != "string" ? core + "?" : core;
            }
        }

        // --------- Stubs (implement later if needed) ----------
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

    // Optional helper (kept for compatibility)
    public static class TableInfoListExtensions
    {
        public static void AddIfMissing(this List<TableInfo> list, TableInfo ti)
        {
            if (!list.Any(x => x.Name.Equals(ti.Name, StringComparison.OrdinalIgnoreCase) &&
                               (x.Schema ?? "dbo").Equals(ti.Schema ?? "dbo", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(ti);
            }
        }
    }
}
