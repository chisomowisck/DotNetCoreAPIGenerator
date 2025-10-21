#nullable enable
using System;
using System.Collections.Generic;
using System.IO;                
using System.Linq;           
using System.Text.RegularExpressions;

namespace Db2Crud.Discovery;

internal static class EntityDiscovery
{
    private static readonly Regex Rx =
        new(@"DbSet<\s*([A-Za-z_][A-Za-z0-9_\.]*)\s*>\s*\w+\s*{", RegexOptions.Multiline);

    public static List<string> ExtractEntityNamesFromDbContext(string dbContextPath)
    {
        if (!File.Exists(dbContextPath))
            throw new FileNotFoundException($"DbContext file not found: {dbContextPath}");

        var code = File.ReadAllText(dbContextPath);
        var names = Rx.Matches(code).Select(m => m.Groups[1].Value).ToList();
        return names.Select(n => n.Split('.').Last()).Distinct().ToList();
    }
}
