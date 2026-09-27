using Coppertop.Api.Data;
using Coppertop.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Coppertop") ?? "Data Source=coppertop.db";
builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.Services.GetRequiredService<Database>().Initialize();

// Shared-secret auth between services. Leave Api:Key empty to disable (local dev only).
var apiKey = app.Configuration["Api:Key"];
if (!string.IsNullOrEmpty(apiKey))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/health")
            || KeyMatches(context.Request.Headers["X-Api-Key"].ToString(), apiKey))
        {
            await next();
            return;
        }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    });
}

app.MapCoppertopEndpoints();

app.Run();

static bool KeyMatches(string provided, string expected) =>
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(provided), System.Text.Encoding.UTF8.GetBytes(expected));

public partial class Program;
