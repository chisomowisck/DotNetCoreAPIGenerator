#nullable enable
using Db2Crud; // TableInfo, ColumnInfo
using Scriban;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Db2Crud.Generation;

internal sealed class CrudRenderer
{
    private readonly TemplateRepository _repo;
    private readonly TemplateRenderer _renderer;

    public CrudRenderer(TemplateRepository repo, TemplateRenderer renderer)
    {
        _repo = repo;
        _renderer = renderer;
    }

    public void RenderAll(
        List<TableInfo> tables,
        string projectPath,
        string contextName,
        bool verbose)
    {
        var rootns = GetRootNamespace(projectPath);

        // Load templates from disk (these should use t.EntityName)
        var controllerTpl = _repo.Load("Controller.sbn");
        var interfaceTpl = _repo.Load("Interface.sbn");
        var serviceTpl = _repo.Load("Service.sbn");
        var dtoTpl = _repo.Load("Dtos.sbn");
        var diTpl = _repo.Load("DiRegistration.sbn");

        // Ensure folders
        Dir(projectPath, "Controllers");
        Dir(projectPath, "Interfaces");
        Dir(projectPath, "Services");
        Dir(projectPath, "Dtos");
        Dir(projectPath, "Infrastructure/DependencyInjection");

        // Clean old aggregated files (if any)
        TryDelete(projectPath, "Controllers/CrudControllers.g.cs");
        TryDelete(projectPath, "Interfaces/CrudInterfaces.g.cs");
        TryDelete(projectPath, "Services/CrudServices.g.cs");
        TryDelete(projectPath, "Dtos/CrudDtos.g.cs");

        // Exclude views from API generation
        var apiTables = tables.Where(x => !x.IsView).ToList();

        foreach (var ti in apiTables)
        {
            // Ensure we have a singular entity name (fallback if SchemaReader didn't set it)
            var entityName = !string.IsNullOrWhiteSpace(ti.EntityName)
                ? ti.EntityName!
                : FallbackSingularize(ti.Name);

            if (string.IsNullOrWhiteSpace(entityName))
                continue; // nothing sensible to generate

            var cols = ti.Columns ?? new List<ColumnInfo>();
            var keyCol = string.IsNullOrWhiteSpace(ti.KeyColumn) ? (cols.FirstOrDefault()?.Name ?? "Id") : ti.KeyColumn!;
            var keyClr = cols.FirstOrDefault(c => c.Name == keyCol)?.ClrType ?? "int";

            var itemModel = new
            {
                t = new
                {
                    EntityName = entityName, // singular EF class name
                    Name = ti.Name,    // physical table (may be plural)
                    KeyColumn = keyCol,
                    KeyClrType = keyClr,
                    Columns = cols
                },
                rootns,
                contextName
            };

            // Use EntityName for output filenames
            Write(projectPath, $"Controllers/{entityName}Controller.g.cs", _renderer.Render(controllerTpl, itemModel), verbose);
            Write(projectPath, $"Interfaces/I{entityName}Service.g.cs", _renderer.Render(interfaceTpl, itemModel), verbose);
            Write(projectPath, $"Services/{entityName}Service.g.cs", _renderer.Render(serviceTpl, itemModel), verbose);
            Write(projectPath, $"Dtos/{entityName}Dtos.g.cs", _renderer.Render(dtoTpl, itemModel), verbose);
        }

        // DI model projected to what the template needs (EntityName)
        var diModel = new
        {
            tables = apiTables.Select(ti => new { EntityName = string.IsNullOrWhiteSpace(ti.EntityName) ? FallbackSingularize(ti.Name) : ti.EntityName }).ToList(),
            rootns,
            contextName
        };
        Write(projectPath, "Infrastructure/DependencyInjection/ServiceRegistration.g.cs",
              _renderer.Render(diTpl, diModel), verbose);
    }

    // --- helpers -------------------------------------------------------------

    private static string FallbackSingularize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        // very light heuristic – SchemaReader should ideally provide EntityName already
        if (name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && name.Length > 3)
            return name[..^3] + "y";
        if (name.EndsWith("ses", StringComparison.OrdinalIgnoreCase))  // e.g. "Processes" -> "Process"
            return name[..^2];
        if (name.EndsWith("es", StringComparison.OrdinalIgnoreCase))
            return name[..^2];
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            return name[..^1];
        return name;
    }

    private static void Dir(string root, string rel) => Directory.CreateDirectory(Path.Combine(root, rel));

    private static void TryDelete(string root, string rel)
    {
        var p = Path.Combine(root, rel);
        if (File.Exists(p)) File.Delete(p);
    }

    private static void Write(string root, string rel, string content, bool verbose)
    {
        var p = Path.Combine(root, rel);
        File.WriteAllText(p, content);
        if (verbose) Console.WriteLine($"wrote {rel}");
    }

    private static string GetRootNamespace(string projectPath
    )
    {
        var full = Path.GetFullPath(projectPath);
        var csproj = Directory.GetFiles(full, "*.csproj").FirstOrDefault();
        if (csproj is not null)
        {
            var txt = File.ReadAllText(csproj);
            var m = Regex.Match(txt, @"<RootNamespace>\s*([^<\s]+)\s*</RootNamespace>", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return Path.GetFileNameWithoutExtension(csproj);
        }
        return new DirectoryInfo(full).Name;
    }
}
