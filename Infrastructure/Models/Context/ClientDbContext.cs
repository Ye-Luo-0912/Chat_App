using Microsoft.EntityFrameworkCore;
using Core.Models;
using Infrastructure.Data;

namespace Infrastructure.Models.Context;

public class ClientDbContext(DbContextOptions<ClientDbContext> options) : DbContext(options)
{
    public DbSet<LocalUser> Users { get; set; }
    public DbSet<ServerEndpoint> Servers { get; set; }
    public DbSet<AuthToken> Tokens { get; set; }
    public DbSet<LocalFriend> Friends { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalUser>()
            .HasKey(x => x.UserId);
        
        modelBuilder.Entity<AuthToken>()
            .HasKey(x => x.Id);
        
        modelBuilder.Entity<LocalFriend>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<ServerEndpoint>()
            .HasIndex(s => new { s.ServerIpAddress, s.ServerPort });
        
        base.OnModelCreating(modelBuilder);
    }
}