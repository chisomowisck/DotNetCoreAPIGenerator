#nullable enable
using Db2Crud; // TableInfo, ColumnInfo
using Db2Crud.Generation;
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

        // load templates from disk
        var controllerTpl = _repo.Load("Controller.sbn");
        var interfaceTpl = _repo.Load("Interface.sbn");
        var serviceTpl = _repo.Load("Service.sbn");
        var dtoTpl = _repo.Load("Dtos.sbn");
        var diTpl = _repo.Load("DiRegistration.sbn");

        // ensure folders
        Dir(projectPath, "Controllers");
        Dir(projectPath, "Interfaces");
        Dir(projectPath, "Services");
        Dir(projectPath, "Dtos");
        Dir(projectPath, "Infrastructure/DependencyInjection");

        // remove old aggregated files (if any)
        TryDelete(projectPath, "Controllers/CrudControllers.g.cs");
        TryDelete(projectPath, "Interfaces/CrudInterfaces.g.cs");
        TryDelete(projectPath, "Services/CrudServices.g.cs");
        TryDelete(projectPath, "Dtos/CrudDtos.g.cs");

        // exclude views from API generation
        var apiTables = tables.Where(t => !t.IsView).ToList();

        foreach (var t in apiTables)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;

            var cols = t.Columns ?? new List<ColumnInfo>();
            var keyCol = string.IsNullOrWhiteSpace(t.KeyColumn) ? (cols.FirstOrDefault()?.Name ?? "Id") : t.KeyColumn!;
            var keyClr = cols.FirstOrDefault(c => c.Name == keyCol)?.ClrType ?? "int";

            var itemModel = new
            {
                t = new { Name = t.Name, KeyColumn = keyCol, KeyClrType = keyClr, Columns = cols },
                rootns,
                contextName
            };

            Write(projectPath, $"Controllers/{t.Name}Controller.g.cs", _renderer.Render(controllerTpl, itemModel), verbose);
            Write(projectPath, $"Interfaces/I{t.Name}Service.g.cs", _renderer.Render(interfaceTpl, itemModel), verbose);
            Write(projectPath, $"Services/{t.Name}Service.g.cs", _renderer.Render(serviceTpl, itemModel), verbose);
            Write(projectPath, $"Dtos/{t.Name}Dtos.g.cs", _renderer.Render(dtoTpl, itemModel), verbose);
        }

        var diModel = new { tables = apiTables, rootns, contextName };
        Write(projectPath, "Infrastructure/DependencyInjection/ServiceRegistration.g.cs",
              _renderer.Render(diTpl, diModel), verbose);
    }

    // helpers
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

    private static string GetRootNamespace(string projectPath)
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
