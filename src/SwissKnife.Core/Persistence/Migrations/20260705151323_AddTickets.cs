using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwissKnife.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    IsInternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketComments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketNumberSequences",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastNumber = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketNumberSequences", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "TicketRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceTicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetTicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketRelationships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketSlaPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketSlaPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    Impact = table.Column<string>(type: "TEXT", nullable: false),
                    Urgency = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    Subcategory = table.Column<string>(type: "TEXT", nullable: true),
                    AssigneeUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TeamOrgUnitId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequesterEmail = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseDueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolutionDueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstRespondedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SlaResponseBreached = table.Column<bool>(type: "INTEGER", nullable: false),
                    SlaResolutionBreached = table.Column<bool>(type: "INTEGER", nullable: false),
                    SlaPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReopenedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketWatchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketWatchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketWatchers_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketRelationships_SourceTicketId_TargetTicketId_Type",
                table: "TicketRelationships",
                columns: new[] { "SourceTicketId", "TargetTicketId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaPolicies_TenantId_Priority",
                table: "TicketSlaPolicies",
                columns: new[] { "TenantId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketWatchers_TicketId",
                table: "TicketWatchers",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Number",
                table: "Tickets",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ResolutionDueAt",
                table: "Tickets",
                columns: new[] { "TenantId", "ResolutionDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ResponseDueAt",
                table: "Tickets",
                columns: new[] { "TenantId", "ResponseDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Status",
                table: "Tickets",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketComments");

            migrationBuilder.DropTable(
                name: "TicketNumberSequences");

            migrationBuilder.DropTable(
                name: "TicketRelationships");

            migrationBuilder.DropTable(
                name: "TicketSlaPolicies");

            migrationBuilder.DropTable(
                name: "TicketWatchers");

            migrationBuilder.DropTable(
                name: "Tickets");
        }
    }
}
