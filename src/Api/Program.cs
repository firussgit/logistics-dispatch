using System.Text.Json.Serialization;
using LogisticsDispatch.Api.Middleware;
using LogisticsDispatch.Infrastructure;
using LogisticsDispatch.Infrastructure.Data;
using LogisticsDispatch.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<DispatchHub>("/hubs/dispatch");

app.Run();

public partial class Program;
