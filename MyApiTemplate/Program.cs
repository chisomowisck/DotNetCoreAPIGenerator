using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using MyApiTemplate.Infrastructure.DependencyInjection;
using MyApiTemplate.Utils;


var builder = WebApplication.CreateBuilder(args);

// Global config
builder.Services.AddSingleton<GlobalConfig>();

// DbContext
//builder.Services.AddDbContext<SampleDbContext>((services, options) =>
//{
//    var config = services.GetRequiredService<GlobalConfig>();
//    options.UseSqlServer(config.ConnectionString);
//});

// MVC + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger config must be before builder.Build()
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Version = "v1",
        Title = "Sample API",
        Description = "API for Sample System"
    });
});

// Generated services
builder.Services.AddGeneratedCrudServices();

// Build the app *after* all services are added
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Sample API v1");
    });
}

app.MapControllers();
app.Run();
