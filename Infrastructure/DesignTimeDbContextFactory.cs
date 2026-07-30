using Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    public ClientDbContext CreateDbContext(string[] args)
    {
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Data");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "ChatApp.db");
        var connectionString = $"Data Source={dbPath};Cache=Shared;Journal Mode=WAL;Synchronous=NORMAL;Busy Timeout=5000;Foreign Keys=ON;";

        var optionsBuilder = new DbContextOptionsBuilder<ClientDbContext>();
        optionsBuilder.UseSqlite(connectionString); // 设计时使用与运行时一致的用户数据目录
        return new ClientDbContext(optionsBuilder.Options);
    }
    
}