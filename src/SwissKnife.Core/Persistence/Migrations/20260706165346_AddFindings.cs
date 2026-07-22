using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwissKnife.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Module = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionReason = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LinkedTicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Findings_TenantId_Fingerprint",
                table: "Findings",
                columns: new[] { "TenantId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Findings_TenantId_Module_Status_Severity",
                table: "Findings",
                columns: new[] { "TenantId", "Module", "Status", "Severity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Findings");
        }
    }
}
