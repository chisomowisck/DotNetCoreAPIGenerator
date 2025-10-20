#nullable enable
using Db2Crud; // SchemaReader, TableInfo, Options (from your other files)
using Scriban;
using Scriban.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { PrintHelp(); return 0; }
            var opts = Parse(args);

            Console.WriteLine($"db2crud starting for project: {opts.Project}");
            Console.WriteLine($"Provider: {opts.Provider}");
            Console.WriteLine();

            Step("[1/5] Ensuring EF & Swagger packages", () =>
            {
                EnsureEfProvider(opts.Provider, opts.Project, opts.Verbose);
            });

            Step("[2/5] Reverse-engineering EF model", () =>
            {
                Run("dotnet",
                    $"ef dbcontext scaffold \"{opts.Conn}\" {opts.Provider} --output-dir Entities --context-dir Data -c {opts.ContextName} --use-database-names --no-onconfiguring --force",
                    opts.Project,
                    opts.Verbose);
            });

            var dbContextPath = Path.Combine(opts.Project, "Data", $"{opts.ContextName}.cs");

            var entityNames = Step("[3/5] Discovering entities", () =>
            {
                var names = ExtractEntityNamesFromDbContext(dbContextPath);
                Console.WriteLine($"Found {names.Count} entities.");
                return names;
            });

            if (opts.Verbose)
            {
                Console.WriteLine("Entities discovered: " + string.Join(", ", entityNames));
            }

            var metadata = Step("[4/5] Reading schema metadata", () =>
            {
                var tables = LoadDbMetadata(opts.Provider, opts.Conn, entityNames);
                Console.WriteLine($"Loaded schema for {tables.Count} tables.");
                return tables;
            });

            if (opts.Verbose)
            {
                foreach (var ti in metadata.Take(10))
                {
                    Console.WriteLine($"  table {ti.Name}: key={ti.KeyColumn ?? "(none)"} cols={ti.Columns?.Count ?? 0}");
                }
            }


            Step("[5/5] Rendering CRUD files", () =>
            {
                PrepareEmbeddedTemplates();
                RenderAll(metadata, opts.Project, opts.ContextName, opts.Verbose);
            });

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✔ Generation complete.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Generation failed:");
            Console.WriteLine(ex.Message);
            Console.ResetColor();
            return 1;
        }
    }

    // -----------------------------
    // Process runner (streams output live)
    // -----------------------------
    private static void Run(string exe, string arguments, string workingDir, bool verbose = false)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"> {exe} {arguments}");
            Console.ResetColor();
        }

        var sw = Stopwatch.StartNew();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };

        if (!proc.Start())
            throw new Exception($"Failed to start: {exe} {arguments}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        proc.WaitForExit();
        sw.Stop();

        if (proc.ExitCode != 0)
            throw new Exception($"Command failed ({proc.ExitCode}): {exe} {arguments}");

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"(done in {sw.Elapsed.TotalSeconds:F1}s)");
            Console.ResetColor();
        }
    }

    // -----------------------------
    // Spinner & Step blocks
    // -----------------------------
    private static T Step<T>(string title, Func<T> action, bool showSpinner = true)
    {
        using var s = Spinner.Start(title, showSpinner);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = action();
            sw.Stop();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{title} (completed in {sw.Elapsed.TotalSeconds:F1}s)");
            Console.ResetColor();
            return result;
        }
        catch
        {
            sw.Stop();
            Console.WriteLine(); // move spinner
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{title} (failed after {sw.Elapsed.TotalSeconds:F1}s)");
            Console.ResetColor();
            throw;
        }
    }
    private static void Step(string title, Action action, bool showSpinner = true)
        => Step(title, () => { action(); return 0; }, showSpinner);

    private static class Spinner
    {
        private static readonly char[] _glyphs = new[] { '|', '/', '-', '\\' };

        public static IDisposable Start(string message, bool enabled = true, int intervalMs = 80)
        {
            if (!enabled)
            {
                Console.WriteLine(message);
                return new Noop();
            }

            var cts = new System.Threading.CancellationTokenSource();
            var t = new System.Threading.Thread(() =>
            {
                int i = 0;
                Console.Write(message + " ");
                while (!cts.IsCancellationRequested)
                {
                    Console.Write(_glyphs[i++ % _glyphs.Length]);
                    System.Threading.Thread.Sleep(intervalMs);
                    Console.Write('\b');
                }
                Console.WriteLine("✓");
            })
            { IsBackground = true };
            t.Start();
            return new Stopper(cts);
        }

        private sealed class Stopper(System.Threading.CancellationTokenSource cts) : IDisposable
        {
            public void Dispose() => cts.Cancel();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    // -----------------------------
    // Core: discover entities, load schema, render files
    // -----------------------------

    private static List<string> ExtractEntityNamesFromDbContext(string path)
    {
        var code = File.ReadAllText(path);

        // Match: DbSet<Foo> Foo { get; set; }
        var rx = new Regex(@"DbSet<\s*([A-Za-z_][A-Za-z0-9_\.]*)\s*>\s*\w+\s*{", RegexOptions.Multiline);
        var names = rx.Matches(code).Select(m => m.Groups[1].Value).ToList();

        // strip possible namespace if EF used fully qualified type inside DbSet<>
        names = names.Select(n => n.Split('.').Last()).Distinct().ToList();

        return names;
    }


    private static List<TableInfo> LoadDbMetadata(string provider, string conn, IEnumerable<string> entities)
        => SchemaReader.Load(provider, conn, entities);

    private static string RenderTpl(Template tpl, object model)
    {
        // Preserve PascalCase names like "Name", "KeyClrType", "Columns"
        var ctx = new TemplateContext
        {
            MemberRenamer = member => member.Name,   // <-- critical
            StrictVariables = true                   // throw if a symbol is missing
        };

        var globals = new Scriban.Runtime.ScriptObject();
        globals.Import(model, renamer: m => m.Name); // keep casing on import
        ctx.PushGlobal(globals);

        return tpl.Render(ctx);
    }

    static void RenderAll(List<TableInfo> tables, string projectPath, string contextName, bool verbose = true)
    {
        //string rootns = Path.GetFileName(projectPath);
        string rootns = GetRootNamespace(projectPath);

        var tdir = "Templates";
        var controllerTpl = Template.Parse(File.ReadAllText(Path.Combine(tdir, "Controller.sbn")));
        var interfaceTpl = Template.Parse(File.ReadAllText(Path.Combine(tdir, "Interface.sbn")));
        var serviceTpl = Template.Parse(File.ReadAllText(Path.Combine(tdir, "Service.sbn")));
        var dtoTpl = Template.Parse(File.ReadAllText(Path.Combine(tdir, "Dtos.sbn")));
        var diTpl = Template.Parse(File.ReadAllText(Path.Combine(tdir, "DiRegistration.sbn")));

        Dir(projectPath, "Controllers");
        Dir(projectPath, "Interfaces");
        Dir(projectPath, "Services");
        Dir(projectPath, "Dtos");
        Dir(projectPath, "Infrastructure/DependencyInjection");

        // clean old aggregated files if they exist
        TryDelete(projectPath, "Controllers/CrudControllers.g.cs");
        TryDelete(projectPath, "Interfaces/CrudInterfaces.g.cs");
        TryDelete(projectPath, "Services/CrudServices.g.cs");
        TryDelete(projectPath, "Dtos/CrudDtos.g.cs");

        // Filter out views for API generation
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

            Write(projectPath, $"Controllers/{t.Name}Controller.g.cs", RenderTpl(controllerTpl, itemModel), verbose);
            Write(projectPath, $"Interfaces/I{t.Name}Service.g.cs", RenderTpl(interfaceTpl, itemModel), verbose);
            Write(projectPath, $"Services/{t.Name}Service.g.cs", RenderTpl(serviceTpl, itemModel), verbose);
            Write(projectPath, $"Dtos/{t.Name}Dtos.g.cs", RenderTpl(dtoTpl, itemModel), verbose);
        }

        var diModel = new { tables = apiTables, rootns, contextName };
        Write(projectPath, "Infrastructure/DependencyInjection/ServiceRegistration.g.cs",
              RenderTpl(diTpl, diModel), verbose);
    }

    private static string GetRootNamespace(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        // 1) Try .csproj
        var csproj = Directory.GetFiles(full, "*.csproj").FirstOrDefault();
        if (csproj is not null)
        {
            var txt = File.ReadAllText(csproj);
            var m = Regex.Match(txt, @"<RootNamespace>\s*([^<\s]+)\s*</RootNamespace>", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return Path.GetFileNameWithoutExtension(csproj);
        }
        // 2) Fallback: folder name
        return new DirectoryInfo(full).Name;
    }

    static void TryDelete(string root, string rel)
    {
        var path = Path.Combine(root, rel);
        if (File.Exists(path)) File.Delete(path);
    }


    private static void Dir(string root, string rel)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(path);
    }

    private static void Write(string root, string rel, string content, bool verbose)
    {
        var path = Path.Combine(root, rel);
        File.WriteAllText(path, content);
        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"wrote {rel}");
            Console.ResetColor();
        }
    }

    // -----------------------------
    // Package setup & templates
    // -----------------------------
    private static void EnsureEfProvider(string provider, string projectPath, bool verbose)
    {
        // dotnet-ef tool (global)
        try { Run("dotnet", "tool install --global dotnet-ef", projectPath, verbose); } catch { }

        // EF provider (e.g., Microsoft.EntityFrameworkCore.SqlServer)
        try { Run("dotnet", $"add package {provider}", projectPath, verbose); } catch { }

        // EF design-time
        try { Run("dotnet", "add package Microsoft.EntityFrameworkCore.Design", projectPath, verbose); } catch { }

        // Swagger so Program.cs compiles out-of-the-box
        try { Run("dotnet", "add package Swashbuckle.AspNetCore", projectPath, verbose); } catch { }
        //try { Run("dotnet", "add package Swashbuckle.AspNetCore --version 6.6.2", projectPath, verbose); } catch { }
    }

    static void PrepareEmbeddedTemplates()
    {
        var dir = "Templates";
        Directory.CreateDirectory(dir);

        // ALWAYS overwrite to avoid stale templates
        void write(string name, string content)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        // Controller: single-entity template
        write("Controller.sbn", """
using Microsoft.AspNetCore.Mvc;
using {{ rootns }}.Common;
using {{ rootns }}.Interfaces;

namespace {{ rootns }}.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class {{ t.Name }}Controller : ControllerBase
{
    private readonly I{{ t.Name }}Service _svc;
    public {{ t.Name }}Controller(I{{ t.Name }}Service svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>>> GetAll()
        => Ok(await _svc.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>> GetById({{ t.KeyClrType }} id)
        => Ok(await _svc.GetByIdAsync(id));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>> Create([FromBody] {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}CreateDto dto)
        => Ok(await _svc.CreateAsync(dto));

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>> Update({{ t.KeyClrType }} id, [FromBody] {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}UpdateDto dto)
        => Ok(await _svc.UpdateAsync(id, dto));

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete({{ t.KeyClrType }} id)
        => Ok(await _svc.DeleteAsync(id));
}
""");

        // Interface: single-entity template
        write("Interface.sbn", """
using {{ rootns }}.Common;

namespace {{ rootns }}.Interfaces;

public interface I{{ t.Name }}Service
{
    Task<ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>> GetAllAsync();
    Task<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>> GetByIdAsync({{ t.KeyClrType }} id);
    Task<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>> CreateAsync({{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}CreateDto dto);
    Task<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>> UpdateAsync({{ t.KeyClrType }} id, {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}UpdateDto dto);
    Task<ApiResponse<bool>> DeleteAsync({{ t.KeyClrType }} id);
}
""");


        // Service: single-entity template
        write("Service.sbn", """
using Microsoft.EntityFrameworkCore;
using {{ rootns }}.Common;
using {{ rootns }}.Data;
using {{ rootns }}.Interfaces;

namespace {{ rootns }}.Services;

[System.CodeDom.Compiler.GeneratedCode("db2crud","1.0.0")]
public partial class {{ t.Name }}Service : I{{ t.Name }}Service
{
    private readonly {{ contextName }} _db;
    public {{ t.Name }}Service({{ contextName }} db) => _db = db;

    public async Task<ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>> GetAllAsync()
    {
        var items = await _db.Set<{{ rootns }}.Entities.{{ t.Name }}>().AsNoTracking().ToListAsync();
        return ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>>.Ok(items.Select(MapToReadDto));
    }

    public async Task<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>> GetByIdAsync({{ t.KeyClrType }} id)
    {
        var entity = await _db.Set<{{ rootns }}.Entities.{{ t.Name }}>().FindAsync(id);
        if (entity == null) return ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>.Fail("Not found");
        return ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>.Ok(MapToReadDto(entity));
    }

    public async Task<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>> CreateAsync({{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}CreateDto dto)
    {
        var entity = MapFromCreateDto(dto);
        _db.Set<{{ rootns }}.Entities.{{ t.Name }}>().Add(entity);
        await _db.SaveChangesAsync();
        return ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>.Ok(MapToReadDto(entity));
    }

    public async Task<ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>> UpdateAsync({{ t.KeyClrType }} id, {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}UpdateDto dto)
    {
        var entity = await _db.Set<{{ rootns }}.Entities.{{ t.Name }}>().FindAsync(id);
        if (entity == null) return ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>.Fail("Not found");
        MapIntoEntity(entity, dto);
        await _db.SaveChangesAsync();
        return ApiResponse<{{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto>.Ok(MapToReadDto(entity));
    }

    public async Task<ApiResponse<bool>> DeleteAsync({{ t.KeyClrType }} id)
    {
        var entity = await _db.Set<{{ rootns }}.Entities.{{ t.Name }}>().FindAsync(id);
        if (entity == null) return ApiResponse<bool>.Fail("Not found");
        _db.Remove(entity);
        await _db.SaveChangesAsync();
        return ApiResponse<bool>.Ok(true);
    }

    private static {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto MapToReadDto({{ rootns }}.Entities.{{ t.Name }} e) => new {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}ReadDto(
        {{~ for c in t.Columns ~}}
        e.{{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
        {{~ end ~}}
    );

    private static {{ rootns }}.Entities.{{ t.Name }} MapFromCreateDto({{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}CreateDto d) => new {{ rootns }}.Entities.{{ t.Name }} {
        {{~ for c in t.Columns ~}}
        {{- if c.Name != t.KeyColumn -}}
        {{ c.CsName ?? c.Name }} = d.{{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
        {{- end -}}
        {{~ end ~}}
    };

    private static void MapIntoEntity({{ rootns }}.Entities.{{ t.Name }} e, {{ rootns }}.Dtos.{{ t.Name }}.{{ t.Name }}UpdateDto d)
    {
        {{~ for c in t.Columns ~}}
        {{- if c.Name != t.KeyColumn -}}
        e.{{ c.CsName ?? c.Name }} = d.{{ c.CsName ?? c.Name }};
        {{- end -}}
        {{~ end ~}}
    }
}
""");


        // DTOs: single entity, one file (no subfolders)
        write("Dtos.sbn", """
namespace {{ rootns }}.Dtos.{{ t.Name }};

public record {{ t.Name }}ReadDto(
{{~ for c in t.Columns ~}}
    {{ c.ClrType }} {{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
{{~ end ~}}
);

public record {{ t.Name }}CreateDto(
{{~ for c in t.Columns ~}}
    {{- if c.Name != t.KeyColumn -}}
    {{ c.ClrType }} {{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
    {{- end -}}
{{~ end ~}}
);

public record {{ t.Name }}UpdateDto(
{{~ for c in t.Columns ~}}
    {{- if c.Name != t.KeyColumn -}}
    {{ c.ClrType }} {{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
    {{- end -}}
{{~ end ~}}
);
""");


        // DI: aggregated
        write("DiRegistration.sbn", """
using Microsoft.Extensions.DependencyInjection;

namespace {{ rootns }}.Infrastructure.DependencyInjection;

public static partial class ServiceRegistration
{
    static partial void AddGeneratedCrudServicesInternal(IServiceCollection services)
    {
        {{~ for t in tables ~}}
        services.AddScoped<{{ rootns }}.Interfaces.I{{ t.Name }}Service, {{ rootns }}.Services.{{ t.Name }}Service>();
        {{~ end ~}}
    }
}
""");
    }

    // -----------------------------
    // CLI Parse & Help
    // -----------------------------
    private static void PrintHelp()
    {
        Console.WriteLine("""
db2crud --provider <EFProviderPackage> --conn <ConnectionString> --context-name <DbContext> --project <ApiProjectPath> [--include "TableA,TableB"] [--verbose]
Example:
  db2crud --provider "Microsoft.EntityFrameworkCore.SqlServer" --conn "Server=.;Database=Store;Trusted_Connection=True;TrustServerCertificate=True" --context-name "StoreDbContext" --project "." --verbose
""");
    }

    private static Options Parse(string[] args)
    {
        string get(string key, string def = "")
        {
            var idx = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : def;
        }

        var provider = get("--provider");
        var conn = get("--conn");
        var project = get("--project", ".");
        var ctx = get("--context-name", "AppDbContext");
        var include = get("--include", "");
        var verbose = Array.Exists(args, a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(conn))
            throw new ArgumentException("Missing required --provider or --conn.");

        return new Options(provider, conn, project, ctx, include, verbose);
    }
}
