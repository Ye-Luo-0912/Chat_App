using Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    public ClientDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ClientDbContext>();
        optionsBuilder.UseSqlite("Data Source=./Data/ChatApp.db;Cache=Shared;"); // 设计时临时数据库
        return new ClientDbContext(optionsBuilder.Options);
    }
    
}