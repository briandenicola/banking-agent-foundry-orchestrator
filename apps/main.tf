locals {
  core_rg_name     = "${var.app_name}-rg"
  apps_rg_name     = "${var.app_name}-apps-rg"
  acr_name         = "${replace(var.app_name, "-", "")}acr"
  cae_name         = "${var.app_name}-env"
  foundry_name     = "${var.app_name}-foundry"
  foundry_project  = "${var.app_name}-project"
  model_deployment = "gpt-5.4-mini"
  # Foundry memory stores require an embedding deployment in addition to the
  # chat model.
  embedding_deployment = "text-embedding-3-small"

  # The deployer treats an empty name as "feature off", so the flags collapse
  # to a single value that both Terraform and the deployer agree on.
  memory_store_name = var.enable_agent_memory ? "customer_profile_memory" : ""
  memory_agent_name = "customer-profile"

  # The toolbox serves the hosted container agents, which have no declarative
  # tools array and authenticate to this MCP endpoint with their own identity.
  # The customer-profile prompt agent does NOT use it: Foundry cannot bind a
  # prompt agent's `mcp` tool to the agent identity, so the call is rejected
  # with a 401 at invocation time. That agent declares its tools inline in
  # `memory_agent_tools` below.
  toolbox_name = var.enable_agent_toolbox ? "banking-toolbox" : ""

  # Foundry rejects a toolbox version when more than one tool lacks an
  # identifier, so every tool carries an explicit unique `name`.
  toolbox_tools = [
    {
      type        = "code_interpreter"
      name        = "transaction_calculator"
      description = "Run Python to compute totals, averages, and date maths over transaction data the customer supplied."
    },
    {
      type = "toolbox_search"
      name = "banking_knowledge_search"
    },
  ]

  # Tool calls are not gated by a Foundry approval prompt. The tools attached
  # here are read-only and computational, and the workflow's own approval gate
  # in the C# orchestrator remains the control that matters. Revisit this if a
  # state-changing tool is ever added to the toolbox.
  toolbox_require_approval = "never"

  # Tools attached directly to the customer-profile prompt agent. Foundry runs
  # the prompt agent's tool loop itself, so managed tools are declared inline
  # rather than reached through the toolbox. Kept in step with `toolbox_tools`
  # so both agent kinds can do the same arithmetic over customer-supplied data.
  memory_agent_tools = var.enable_agent_toolbox ? [
    {
      type = "code_interpreter"
    },
  ] : []

  # Memory extraction is model-driven. In a banking assistant the conversation
  # is full of exactly the data that must not be retained, so the exclusion
  # instruction is explicit configuration rather than a default.
  memory_user_profile_details = join(" ", [
    "Retain only servicing preferences such as preferred contact channel,",
    "language, accessibility needs, and communication tone.",
    "Never retain account numbers, card numbers, balances, transaction",
    "amounts, financial details, government identifiers, credentials,",
    "precise location, date of birth, or age.",
  ])

  # "Do not retain" and "do not use" are separated deliberately. Collapsing them
  # into a single prohibition makes the agent refuse to calculate over figures
  # the customer just supplied, which is the one thing its code interpreter
  # exists to do, so the tool sits there unusable.
  memory_agent_instructions = join(" ", [
    "You are a retail banking servicing assistant.",
    "Use remembered servicing preferences to personalise how you respond.",
    "You may calculate over figures the customer provides in the current",
    "conversation and show those results back to them. Use the code",
    "interpreter for arithmetic rather than working it out yourself, and show",
    "the code when asked.",
    "Never retain any of it: account numbers, card numbers, balances,",
    "transaction amounts, financial details and government identifiers must",
    "never be written to memory or recalled in a later conversation.",
    "You provide guidance only; you never approve, action, or commit to any",
    "account change. Direct the customer to a banker for actions.",
  ])

  orchestrator_app_name = "${var.app_name}-orchestrator"
  webui_app_name        = "${var.app_name}-webui"

  # The orchestrator's internal FQDN, constructed rather than read from
  # azurerm_container_app.orchestrator.ingress[0].fqdn.
  #
  # This is deliberate. Switching ingress from external to internal changes the
  # FQDN, but the provider does not mark that computed attribute as unknown, so
  # Terraform would plan the Web UI using the *old* external FQDN and leave it
  # pointing at a hostname that no longer resolves. The Web UI would lose the
  # orchestrator until a second apply corrected the drift.
  #
  # Container Apps derives both forms from the environment's default domain:
  #   external: <app>.<default_domain>
  #   internal: <app>.internal.<default_domain>
  # Building it here makes the value known at plan time, so one apply is enough.
  orchestrator_internal_fqdn = "${local.orchestrator_app_name}.internal.${data.azurerm_container_app_environment.this.default_domain}"
  orchestrator_internal_url  = "https://${local.orchestrator_internal_fqdn}"

  orchestrator_image      = "${data.azurerm_container_registry.this.login_server}/orchestrator:${var.image_tag}"
  webui_image             = "${data.azurerm_container_registry.this.login_server}/webui:${var.image_tag}"
  hosted_agents_image     = "${data.azurerm_container_registry.this.login_server}/hosted-agents:${var.image_tag}"
  agent_deployer_image    = "${data.azurerm_container_registry.this.login_server}/agent-deployer:${var.image_tag}"
  database_migrator_image = "${data.azurerm_container_registry.this.login_server}/database-migrator:${var.image_tag}"

  foundry_project_endpoint = "https://${local.foundry_name}.services.ai.azure.com/api/projects/${local.foundry_project}"
  foundry_openai_endpoint  = "https://${local.foundry_name}.openai.azure.com"
  postgresql_host          = "${var.app_name}-db.postgres.database.azure.com"
  postgresql_database      = "banking_agent"

  foundry_tool_endpoints = jsonencode({
    "workflow.plan"       = "${local.foundry_project_endpoint}/agents/workflow-planning/endpoint/protocols/invocations?api-version=v1"
    "transaction.explain" = "${local.foundry_project_endpoint}/agents/transaction-explanation/endpoint/protocols/invocations?api-version=v1"
    "suspicious.assess"   = "${local.foundry_project_endpoint}/agents/suspicious-activity/endpoint/protocols/invocations?api-version=v1"
    "dispute.plan"        = "${local.foundry_project_endpoint}/agents/dispute-planning/endpoint/protocols/invocations?api-version=v1"
  })

  foundry_mcp_tool_endpoints = jsonencode({
    "workflow.plan"       = "${local.foundry_project_endpoint}/agents/workflow-planning/endpoint/protocols/invocations?api-version=v1"
    "transaction.explain" = "${local.foundry_project_endpoint}/agents/transaction-explanation/endpoint/protocols/invocations?api-version=v1"
    "suspicious.assess"   = "${local.foundry_project_endpoint}/agents/suspicious-activity/endpoint/protocols/invocations?api-version=v1"
    "dispute.plan"        = "${local.foundry_project_endpoint}/agents/dispute-planning/endpoint/protocols/invocations?api-version=v1"
  })

  hosted_agent_definitions = [
    {
      name = "workflow-planning"
      kind = "workflow-planning"
    },
    {
      name = "transaction-explanation"
      kind = "transaction-explanation"
    },
    {
      name = "suspicious-activity"
      kind = "suspicious-activity"
    },
    {
      name = "dispute-planning"
      kind = "dispute-planning"
    }
  ]

  tags = {
    Application = "Banking Agent"
    ManagedBy   = "Terraform"
  }
}

data "azurerm_user_assigned_identity" "database_migrator" {
  name                = "${var.app_name}-database-migrator-identity"
  resource_group_name = local.core_rg_name
}
