#nullable enable
using System;

namespace Db2Crud.Core;

public sealed record Options(
    string Provider,
    string Conn,
    string Project,
    string ContextName,
    string Include,
    bool Verbose,
    string TemplatesDir // always set (defaults to "Templates" if not provided)
)
{
    public static Options Parse(string[] args)
    {
        string Get(string key, string def = "")
        {
            var idx = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : def;
        }

        var provider = Get("--provider");
        var conn = Get("--conn");
        var project = Get("--project", ".");
        var ctx = Get("--context-name", "AppDbContext");
        var include = Get("--include", "");
        var verbose = Array.Exists(args, a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        var templatesDir = Get("--templates", "Templates"); // <— fix: use Get

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(conn))
            throw new ArgumentException("Missing required --provider or --conn.");

        return new Options(provider, conn, project, ctx, include, verbose, templatesDir);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
db2crud --provider <EFProviderPackage> --conn <ConnectionString> --context-name <DbContext> --project <ApiProjectPath>
        [--include "TableA,TableB"] [--verbose] [--templates <TemplatesDir>]

Examples:
  db2crud --provider "Microsoft.EntityFrameworkCore.SqlServer" --conn "Server=.;Database=Store;Trusted_Connection=True;TrustServerCertificate=True" --context-name "StoreDbContext" --project "." --verbose
  db2crud --provider "Microsoft.EntityFrameworkCore.SqlServer" --conn "<conn>" --context-name "StoreDbContext" --project "." --templates ".templates"
""");
    }
}
