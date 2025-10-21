#nullable enable
using Db2Crud.Discovery;
using Db2Crud.Generation;
using System;
using System.IO;
using System.Linq;

namespace Db2Crud.Core
{
    internal sealed class Runner
    {
        public int Run(string[] args)
        {
            try
            {
                if (args.Length == 0) { Options.PrintHelp(); return 0; }
                var opts = Options.Parse(args);

                Console.WriteLine($"db2crud starting for project: {opts.Project}");
                Console.WriteLine($"Provider: {opts.Provider}");
                Console.WriteLine();

                var proc = new ProcessRunner();
                var pkgs = new PackageInstaller(proc);
                var repo = new TemplateRepository(opts.TemplatesDir);
                var render = new TemplateRenderer();
                var crud = new CrudRenderer(repo, render);

                Steps.Run("[1/5] Ensuring EF & Swagger packages", () =>
                {
                    pkgs.EnsureEfAndSwagger(opts.Provider, opts.Project, opts.Verbose);
                });

                Steps.Run("[2/5] Reverse-engineering EF model", () =>
                {
                    pkgs.ReverseEngineer(opts.Conn, opts.Provider, opts.ContextName, opts.Project, opts.Verbose);
                });

                var dbContextPath = Path.Combine(opts.Project, "Data", $"{opts.ContextName}.cs");

                var entityNames = Steps.Run("[3/5] Discovering entities", () =>
                {
                    var names = EntityDiscovery.ExtractEntityNamesFromDbContext(dbContextPath);
                    Console.WriteLine($"Found {names.Count} entities.");
                    if (opts.Verbose) Console.WriteLine("Entities discovered: " + string.Join(", ", names));
                    return names;
                });

                // NEW: Build (Entity, SchemaHint, TableHint) target list
                var targets = EntityDiscovery.ResolveTargets(dbContextPath, entityNames);

                var metadata = Steps.Run("[4/5] Reading schema metadata", () =>
                {
                    var tables = SchemaReader.Load(opts.Provider, opts.Conn, targets);
                    Console.WriteLine($"Loaded schema for {tables.Count} tables.");
                    if (opts.Verbose)
                    {
                        foreach (var ti in tables.Take(10))
                            Console.WriteLine($"  {ti.Schema}.{ti.Name} → Entity {ti.EntityName}: key={ti.KeyColumn ?? "(none)"} cols={ti.Columns?.Count ?? 0}");
                    }
                    return tables;
                });

                Steps.Run("[5/5] Rendering CRUD files", () =>
                {
                    repo.WriteDefaultsIfMissing();
                    crud.RenderAll(metadata, opts.Project, opts.ContextName, opts.Verbose);
                });

                Console.WriteLine();
                var old = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✔ Generation complete.");
                Console.ForegroundColor = old;
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                var old = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Generation failed:");
                Console.WriteLine(ex.Message);
                Console.ForegroundColor = old;
                return 1;
            }
        }
    }
}


//#nullable enable
//using Db2Crud.Discovery;
//using Db2Crud.Generation;
//using System;
//using System.IO;
//using System.Linq;

//namespace Db2Crud.Core;

//internal sealed class Runner
//{
//    public int Run(string[] args)
//    {
//        try
//        {
//            if (args.Length == 0) { Options.PrintHelp(); return 0; }
//            var opts = Options.Parse(args);

//            Console.WriteLine($"db2crud starting for project: {opts.Project}");
//            Console.WriteLine($"Provider: {opts.Provider}");
//            Console.WriteLine();

//            var proc = new ProcessRunner();
//            var pkgs = new PackageInstaller(proc);
//            var repo = new TemplateRepository(opts.TemplatesDir);  // <-- important change
//            var render = new TemplateRenderer();
//            var crud = new CrudRenderer(repo, render);

//            Steps.Run("[1/5] Ensuring EF & Swagger packages", () =>
//            {
//                pkgs.EnsureEfAndSwagger(opts.Provider, opts.Project, opts.Verbose);
//            });

//            Steps.Run("[2/5] Reverse-engineering EF model", () =>
//            {
//                pkgs.ReverseEngineer(opts.Conn, opts.Provider, opts.ContextName, opts.Project, opts.Verbose);
//            });

//            var dbContextPath = Path.Combine(opts.Project, "Data", $"{opts.ContextName}.cs");

//            var entityNames = Steps.Run("[3/5] Discovering entities", () =>
//            {
//                var names = EntityDiscovery.ExtractEntityNamesFromDbContext(dbContextPath);
//                Console.WriteLine($"Found {names.Count} entities.");
//                if (opts.Verbose) Console.WriteLine("Entities discovered: " + string.Join(", ", names));
//                return names;
//            });

//            var metadata = Steps.Run("[4/5] Reading schema metadata", () =>
//            {
//                var tables = SchemaReader.Load(opts.Provider, opts.Conn, entityNames);
//                Console.WriteLine($"Loaded schema for {tables.Count} tables.");
//                if (opts.Verbose)
//                {
//                    foreach (var ti in tables.Take(10))
//                        Console.WriteLine($"  table {ti.Name}: key={ti.KeyColumn ?? "(none)"} cols={ti.Columns?.Count ?? 0}");
//                }
//                return tables;
//            });

//            Steps.Run("[5/5] Rendering CRUD files", () =>
//            {
//                repo.WriteDefaultsIfMissing();  // seeds missing *.sbn files in opts.TemplatesDir
//                crud.RenderAll(metadata, opts.Project, opts.ContextName, opts.Verbose);
//            });

//            Console.WriteLine();
//            var old = Console.ForegroundColor;
//            Console.ForegroundColor = ConsoleColor.Green;
//            Console.WriteLine("✔ Generation complete.");
//            Console.ForegroundColor = old;
//            return 0;
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine();
//            var old = Console.ForegroundColor;
//            Console.ForegroundColor = ConsoleColor.Red;
//            Console.WriteLine("Generation failed:");
//            Console.WriteLine(ex.Message);
//            Console.ForegroundColor = old;
//            return 1;
//        }
//    }
//}
