// Phase 0 skeleton. The real surface is defined in SERVER_SPEC.md and built in Phase 2.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/api/v1/ping", () => Results.Ok(new
{
    product = "Rank Master 2 server",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
    ready = false
}));

app.Run();

public partial class Program;
