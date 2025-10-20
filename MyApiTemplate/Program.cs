using MyApiTemplate.Infrastructure.DependencyInjection; // will become <NewProjectName>.Infrastructure.DependencyInjection

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddGeneratedCrudServices();
// Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Generated services (no-ops until the generator adds *.g.cs)
builder.Services.AddGeneratedCrudServices();

var app = builder.Build();

// Pipeline
app.UseMiddleware<ExceptionHandlingMiddleware>(); // ensure the class is accessible by namespace or in global namespace

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
