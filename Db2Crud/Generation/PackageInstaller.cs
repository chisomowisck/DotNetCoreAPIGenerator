#nullable enable
using Db2Crud.Core;

namespace Db2Crud.Generation;

internal sealed class PackageInstaller
{
    private readonly ProcessRunner _proc;

    public PackageInstaller(ProcessRunner proc) => _proc = proc;

    public void EnsureEfAndSwagger(string provider, string projectPath, bool verbose)
    {
        try { _proc.Run("dotnet", "tool install --global dotnet-ef", projectPath, verbose); } catch { }
        try { _proc.Run("dotnet", $"add package {provider}", projectPath, verbose); } catch { }
        try { _proc.Run("dotnet", "add package Microsoft.EntityFrameworkCore.Design", projectPath, verbose); } catch { }
        try { _proc.Run("dotnet", "add package Swashbuckle.AspNetCore", projectPath, verbose); } catch { }
    }

    public void ReverseEngineer(string conn, string provider, string contextName, string projectPath, bool verbose)
    {
        _proc.Run("dotnet",
            $"ef dbcontext scaffold \"{conn}\" {provider} --output-dir Entities --context-dir Data -c {contextName} --use-database-names --no-onconfiguring --force",
            projectPath, verbose);
    }
}
