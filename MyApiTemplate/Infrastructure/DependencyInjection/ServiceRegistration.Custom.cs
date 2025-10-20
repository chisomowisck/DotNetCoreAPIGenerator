namespace MyApiTemplate.Infrastructure.DependencyInjection; // replaced to <NewProjectName>...

using Microsoft.Extensions.DependencyInjection;

public static partial class ServiceRegistration
{
    public static IServiceCollection AddGeneratedCrudServices(this IServiceCollection services)
    {
        // implemented by generator in ServiceRegistration.g.cs
        AddGeneratedCrudServicesInternal(services);
        return services;
    }

    static partial void AddGeneratedCrudServicesInternal(IServiceCollection services);
}
