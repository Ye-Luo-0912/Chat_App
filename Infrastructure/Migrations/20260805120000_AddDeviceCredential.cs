using Chat_App.Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations;

[DbContext(typeof(ClientDbContext))]
[Migration("20260805120000_AddDeviceCredential")]
public sealed class AddDeviceCredential : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DeviceCredential",
            table: "Tokens",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DeviceCredential",
            table: "Tokens");
    }
}
