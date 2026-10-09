using LogisticsDispatch.Infrastructure.Data;
using LogisticsDispatch.Infrastructure.Realtime;
using LogisticsDispatch.Infrastructure.Repositories;
using LogisticsDispatch.Infrastructure.Routing;
using LogisticsDispatch.Infrastructure.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LogisticsDispatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Dispatch") ?? "Data Source=dispatch.db";
        services.AddDbContext<DispatchDbContext>(o => o.UseSqlite(connection));

        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IDriverRepository, DriverRepository>();
        services.AddScoped<IOfferRepository, OfferRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<User>, Microsoft.AspNetCore.Identity.PasswordHasher<User>>();
        services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DispatchService>();

        services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        services.AddSingleton<IDispatchNotifier, SignalRDispatchNotifier>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<RoutingOptions>(config.GetSection(RoutingOptions.Section));
        services.AddSingleton<RoutingHealth>();
        if (string.Equals(config["Routing:Provider"], "StraightLine", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IRouteProvider, StraightLineRouteProvider>();
        }
        else
        {
            services.AddHttpClient<OsrmRouteProvider>((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<RoutingOptions>>().Value;
                client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });
            services.AddTransient<IRouteProvider>(sp => sp.GetRequiredService<OsrmRouteProvider>());
        }

        services.Configure<SimulationOptions>(config.GetSection(SimulationOptions.Section));
        services.AddHostedService<SimulationWorker>();
        return services;
    }
}
