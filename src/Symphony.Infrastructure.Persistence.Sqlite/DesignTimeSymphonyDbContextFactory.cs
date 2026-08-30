using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Symphony.Infrastructure.Persistence.Sqlite;

// Lets `dotnet ef migrations add` run against this project alone. Booting
// Symphony.Host as the design-time startup project drags every runtime assembly
// into the EF host process, where Windows Application Control has intermittently
// blocked freshly built DLLs (0x800711C7); migrations only need the model.
public sealed class DesignTimeSymphonyDbContextFactory : IDesignTimeDbContextFactory<SymphonyDbContext>
{
    public SymphonyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite("Data Source=design-time-symphony.db")
            .Options;
        return new SymphonyDbContext(options);
    }
}
