using LogisticsDispatch.Infrastructure.Data;
using LogisticsDispatch.Infrastructure.Realtime;
using LogisticsDispatch.Infrastructure.Repositories;
using LogisticsDispatch.Infrastructure.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LogisticsDispatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Dispatch") ?? "Data Source=dispatch.db";
        services.AddDbContext<DispatchDbContext>(o => o.UseSqlite(connection));

        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IDriverRepository, DriverRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DispatchService>();

        services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        services.AddSingleton<IDispatchNotifier, SignalRDispatchNotifier>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<SimulationOptions>(config.GetSection(SimulationOptions.Section));
        services.AddHostedService<SimulationWorker>();
        return services;
    }
}
