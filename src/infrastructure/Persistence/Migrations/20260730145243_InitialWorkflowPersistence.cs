using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWorkflowPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "demo_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    merchant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_suspicious = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_message = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    intent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    approval_decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    approval_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "action_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_executions", x => x.id);
                    table.ForeignKey(
                        name: "FK_action_executions_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_decisions_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "support_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_cases", x => x.id);
                    table.ForeignKey(
                        name: "FK_support_cases_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_events_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_idempotency_key",
                table: "action_executions",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_action_executions_workflow_id",
                table: "action_executions",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_workflow_id",
                table: "approval_decisions",
                column: "workflow_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_support_cases_case_number",
                table: "support_cases",
                column: "case_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_support_cases_workflow_id",
                table: "support_cases",
                column: "workflow_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_events_workflow_id_sequence",
                table: "workflow_events",
                columns: new[] { "workflow_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_trace_id",
                table: "workflows",
                column: "trace_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_executions");

            migrationBuilder.DropTable(
                name: "approval_decisions");

            migrationBuilder.DropTable(
                name: "demo_transactions");

            migrationBuilder.DropTable(
                name: "support_cases");

            migrationBuilder.DropTable(
                name: "workflow_events");

            migrationBuilder.DropTable(
                name: "workflows");
        }
    }
}
