using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

try
{
    Log.Information("AFH Sync Worker started");

    var builder = Host.CreateDefaultBuilder(args);

    builder.UseSerilog();

    // DbContext and Hangfire registration added in Phase 2

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AFH Sync Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
