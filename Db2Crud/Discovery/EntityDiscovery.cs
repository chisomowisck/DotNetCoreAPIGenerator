#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Db2Crud.Discovery
{
    internal static class EntityDiscovery
    {
        private static readonly Regex RxDbSet = new(@"DbSet<\s*([A-Za-z_][A-Za-z0-9_\.]*)\s*>\s*\w+\s*{", RegexOptions.Multiline);

        public static List<string> ExtractEntityNamesFromDbContext(string dbContextPath)
        {
            var code = File.ReadAllText(dbContextPath);
            var names = RxDbSet.Matches(code).Select(m => m.Groups[1].Value).ToList();
            return names.Select(n => n.Split('.').Last()).Distinct().ToList();
        }

        /// <summary>
        /// Build (Entity, SchemaHint, TableHint) tuples.
        /// Tries to read ToTable("Table","Schema") or ToView("View","Schema") from OnModelCreating; 
        /// falls back to (Entity, "", Entity) so SchemaReader can resolve singular/plural against DB.
        /// </summary>
        public static List<(string Entity, string Schema, string Table)> ResolveTargets(string dbContextPath, List<string> entityNames)
        {
            var code = File.ReadAllText(dbContextPath);
            var targets = new List<(string, string, string)>();

            foreach (var e in entityNames)
            {
                string schema = "";
                string table = e;

                // Try: modelBuilder.Entity<e>().ToTable("Table","Schema")
                var rxTable = new Regex($@"Entity<\s*{Regex.Escape(e)}\s*>\s*\(\)\s*\.?\s*ToTable\s*\(\s*""([^""]+)""\s*(,\s*""([^""]+)"")?", RegexOptions.Multiline);
                var m1 = rxTable.Match(code);
                if (m1.Success)
                {
                    table  = m1.Groups[1].Value;
                    if (m1.Groups[3].Success) schema = m1.Groups[3].Value;
                }
                else
                {
                    // Or ToView(...) (we still want the physical object name to find columns, even if you skip views later)
                    var rxView = new Regex($@"Entity<\s*{Regex.Escape(e)}\s*>\s*\(\)\s*\.?\s*ToView\s*\(\s*""([^""]+)""\s*(,\s*""([^""]+)"")?", RegexOptions.Multiline);
                    var m2 = rxView.Match(code);
                    if (m2.Success)
                    {
                        table  = m2.Groups[1].Value;
                        if (m2.Groups[3].Success) schema = m2.Groups[3].Value;
                    }
                }

                targets.Add((e, schema, table));
            }

            return targets;
        }
    }
}


//#nullable enable
//using System;
//using System.Collections.Generic;
//using System.IO;                
//using System.Linq;           
//using System.Text.RegularExpressions;

//namespace Db2Crud.Discovery;

//internal static class EntityDiscovery
//{
//    private static readonly Regex Rx =
//        new(@"DbSet<\s*([A-Za-z_][A-Za-z0-9_\.]*)\s*>\s*\w+\s*{", RegexOptions.Multiline);

//    public static List<string> ExtractEntityNamesFromDbContext(string dbContextPath)
//    {
//        if (!File.Exists(dbContextPath))
//            throw new FileNotFoundException($"DbContext file not found: {dbContextPath}");

//        var code = File.ReadAllText(dbContextPath);
//        var names = Rx.Matches(code).Select(m => m.Groups[1].Value).ToList();
//        return names.Select(n => n.Split('.').Last()).Distinct().ToList();
//    }
//}
