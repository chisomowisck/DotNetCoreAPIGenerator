#nullable enable
using Scriban;
using System;
using System.IO;

namespace Db2Crud.Generation;

internal sealed class TemplateRepository
{
    private readonly string _dir;

    public TemplateRepository(string? dir = null)
    {
        _dir = dir ?? "Templates";
        Directory.CreateDirectory(_dir);
    }

    public string PathFor(string name) => Path.Combine(_dir, name);

    public Template Load(string name) => Template.Parse(File.ReadAllText(PathFor(name)));

    // --- public APIs ---------------------------------------------------------

    // Create default templates only if they don't already exist
    public void WriteDefaultsIfMissing()
    {
        WriteIfMissing("Controller.sbn", ControllerTpl);
        WriteIfMissing("Interface.sbn", InterfaceTpl);
        WriteIfMissing("Service.sbn", ServiceTpl);
        WriteIfMissing("Dtos.sbn", DtosTpl);
        WriteIfMissing("DiRegistration.sbn", DiTpl);
    }

    // Overwrite templates on every run
    public void WriteAllDefaultTemplates()
    {
        WriteFile("Controller.sbn", ControllerTpl);
        WriteFile("Interface.sbn", InterfaceTpl);
        WriteFile("Service.sbn", ServiceTpl);
        WriteFile("Dtos.sbn", DtosTpl);
        WriteFile("DiRegistration.sbn", DiTpl);
    }

    // --- helpers -------------------------------------------------------------

    private void WriteIfMissing(string name, string content)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            File.WriteAllText(path, content);
    }

    private void WriteFile(string name, string content)
        => File.WriteAllText(PathFor(name), content);

    // --- template contents ---------------------------------------------------
    private const string ControllerTpl = """
        using Microsoft.AspNetCore.Mvc;
        using {{ rootns }}.Common;
        using {{ rootns }}.Interfaces;

        namespace {{ rootns }}.Controllers;

        [ApiController]
        [Route("api/[controller]")]
        public partial class {{ t.EntityName }}Controller : ControllerBase
        {
            private readonly I{{ t.EntityName }}Service _svc;
            public {{ t.EntityName }}Controller(I{{ t.EntityName }}Service svc) => _svc = svc;

            [HttpGet]
            public async Task<ActionResult<ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>>> GetAll()
                => Ok(await _svc.GetAllAsync());

            [HttpGet("{id}")]
            public async Task<ActionResult<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>> GetById({{ t.KeyClrType }} id)
                => Ok(await _svc.GetByIdAsync(id));

            [HttpPost]
            public async Task<ActionResult<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>> Create([FromBody] {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}CreateDto dto)
                => Ok(await _svc.CreateAsync(dto));

            [HttpPut("{id}")]
            public async Task<ActionResult<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>> Update({{ t.KeyClrType }} id, [FromBody] {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}UpdateDto dto)
                => Ok(await _svc.UpdateAsync(id, dto));

            [HttpDelete("{id}")]
            public async Task<ActionResult<ApiResponse<bool>>> Delete({{ t.KeyClrType }} id)
                => Ok(await _svc.DeleteAsync(id));
        }
        """;

            private const string InterfaceTpl = """
        using {{ rootns }}.Common;

        namespace {{ rootns }}.Interfaces;

        public interface I{{ t.EntityName }}Service
        {
            Task<ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>> GetAllAsync();
            Task<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>> GetByIdAsync({{ t.KeyClrType }} id);
            Task<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>> CreateAsync({{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}CreateDto dto);
            Task<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>> UpdateAsync({{ t.KeyClrType }} id, {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}UpdateDto dto);
            Task<ApiResponse<bool>> DeleteAsync({{ t.KeyClrType }} id);
        }
        """;

            private const string ServiceTpl = """
        using Microsoft.EntityFrameworkCore;
        using {{ rootns }}.Common;
        using {{ rootns }}.Data;
        using {{ rootns }}.Interfaces;

        namespace {{ rootns }}.Services;

        [System.CodeDom.Compiler.GeneratedCode("db2crud","1.0.0")]
        public partial class {{ t.EntityName }}Service : I{{ t.EntityName }}Service
        {
            private readonly {{ contextName }} _db;
            public {{ t.EntityName }}Service({{ contextName }} db) => _db = db;

            public async Task<ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>> GetAllAsync()
            {
                var items = await _db.Set<{{ rootns }}.Entities.{{ t.EntityName }}>().AsNoTracking().ToListAsync();
                return ApiResponse<IEnumerable<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>>.Ok(items.Select(MapToReadDto));
            }

            public async Task<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>> GetByIdAsync({{ t.KeyClrType }} id)
            {
                var entity = await _db.Set<{{ rootns }}.Entities.{{ t.EntityName }}>().FindAsync(id);
                if (entity == null) return ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>.Fail("Not found");
                return ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>.Ok(MapToReadDto(entity));
            }

            public async Task<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>> CreateAsync({{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}CreateDto dto)
            {
                var entity = MapFromCreateDto(dto);
                _db.Set<{{ rootns }}.Entities.{{ t.EntityName }}>().Add(entity);
                await _db.SaveChangesAsync();
                return ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>.Ok(MapToReadDto(entity));
            }

            public async Task<ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>> UpdateAsync({{ t.KeyClrType }} id, {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}UpdateDto dto)
            {
                var entity = await _db.Set<{{ rootns }}.Entities.{{ t.EntityName }}>().FindAsync(id);
                if (entity == null) return ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>.Fail("Not found");
                MapIntoEntity(entity, dto);
                await _db.SaveChangesAsync();
                return ApiResponse<{{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto>.Ok(MapToReadDto(entity));
            }

            public async Task<ApiResponse<bool>> DeleteAsync({{ t.KeyClrType }} id)
            {
                var entity = await _db.Set<{{ rootns }}.Entities.{{ t.EntityName }}>().FindAsync(id);
                if (entity == null) return ApiResponse<bool>.Fail("Not found");
                _db.Remove(entity);
                await _db.SaveChangesAsync();
                return ApiResponse<bool>.Ok(true);
            }

            private static {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto MapToReadDto({{ rootns }}.Entities.{{ t.EntityName }} e) => new {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}ReadDto(
                {{~ for c in t.Columns ~}}
                e.{{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
                {{~ end ~}}
            );

            private static {{ rootns }}.Entities.{{ t.EntityName }} MapFromCreateDto({{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}CreateDto d) => new {{ rootns }}.Entities.{{ t.EntityName }} {
                {{~ for c in t.Columns ~}}
                {{- if c.Name != t.KeyColumn -}}
                {{ c.CsName ?? c.Name }} = d.{{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
                {{- end -}}
                {{~ end ~}}
            };

            private static void MapIntoEntity({{ rootns }}.Entities.{{ t.EntityName }} e, {{ rootns }}.Dtos.{{ t.EntityName }}.{{ t.EntityName }}UpdateDto d)
            {
                {{~ for c in t.Columns ~}}
                {{- if c.Name != t.KeyColumn -}}
                e.{{ c.CsName ?? c.Name }} = d.{{ c.CsName ?? c.Name }};
                {{- end -}}
                {{~ end ~}}
            }
        }
        """;

            private const string DtosTpl = """
        namespace {{ rootns }}.Dtos.{{ t.EntityName }};

        public record {{ t.EntityName }}ReadDto(
        {{~ for c in t.Columns ~}}
            {{ c.ClrType }} {{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
        {{~ end ~}}
        );

        public record {{ t.EntityName }}CreateDto(
        {{~ for c in t.Columns ~}}
            {{- if c.Name != t.KeyColumn -}}
            {{ c.ClrType }} {{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
            {{- end -}}
        {{~ end ~}}
        );

        public record {{ t.EntityName }}UpdateDto(
        {{~ for c in t.Columns ~}}
            {{- if c.Name != t.KeyColumn -}}
            {{ c.ClrType }} {{ c.CsName ?? c.Name }}{{ if !for.last }},{{ end }}
            {{- end -}}
        {{~ end ~}}
        );
        """;

            private const string DiTpl = """
        using Microsoft.Extensions.DependencyInjection;

        namespace {{ rootns }}.Infrastructure.DependencyInjection;

        public static partial class ServiceRegistration
        {
            static partial void AddGeneratedCrudServicesInternal(IServiceCollection services)
            {
                {{~ for t in tables ~}}
                services.AddScoped<{{ rootns }}.Interfaces.I{{ t.EntityName }}Service, {{ rootns }}.Services.{{ t.EntityName }}Service>();
                {{~ end ~}}
            }
        }
""";

}
