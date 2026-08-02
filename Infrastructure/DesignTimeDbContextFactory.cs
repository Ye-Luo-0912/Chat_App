using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chat_App.Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    public ClientDbContext CreateDbContext(string[] args)
    {
        // 复用共享连接字符串构造器，避免与运行时漂移
        var connectionString = DbPathProvider.BuildConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<ClientDbContext>();
        optionsBuilder.UseSqlite(connectionString);
        return new ClientDbContext(optionsBuilder.Options);
    }
}