using LRN.DataLibrary.Abstractions;
using LRN.DataLibrary.Db;
using LRN.DataLibrary.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LRN.DataLibrary.Registration;

public static class LrnDataLibraryRegistration
{
    /// <summary>
    /// Registers LRN logging DbContext + repository.
    /// 
    /// Looks for connection string in:
    /// 1) ConnectionStrings:LrnLogDb
    /// 2) LrnStepLog:ConnectionString (backward compatible)
    /// </summary>
    public static IServiceCollection AddLrnLogDataLibrary(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("LrnLogDb")
                 ?? config["LrnStepLog:ConnectionString"]
                 ?? "";

        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Missing SQL connection string. Set ConnectionStrings:LrnLogDb (recommended) or LrnStepLog:ConnectionString.");

        services.AddDbContext<LrnLogDbContext>(o => o.UseSqlServer(cs));
        services.AddScoped<ILrnLogRepository, LrnLogRepository>();
        return services;
    }
}
