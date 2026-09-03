variable "app_name" {
  description = "Base resource name from infrastructure output APP_NAME."
  type        = string
}

variable "region" {
  description = "Azure region used by the infrastructure stack."
  type        = string
  default     = "swedencentral"
}

variable "image_tag" {
  description = "Container image tag to deploy."
  type        = string
  default     = "latest"
}

variable "allow_insecure_service_auth" {
  description = "Acknowledge that workflow endpoints will accept unauthenticated callers when enable_service_auth is false. Required in tenants that forbid creating the service principal and api:// identifier URI service authentication depends on. Never set this for real or regulated data."
  type        = bool
  default     = false
}

variable "enable_service_auth" {
  description = "Provision Entra API resources and require Workflow.Invoke tokens for orchestrator workflow endpoints. Defaults secure; disabling is only supported for local development and will not run in deployed Production."
  type        = bool
  default     = true
}

variable "enable_agent_memory" {
  description = "Deploy the customer-profile prompt agent with a Foundry memory store attached. Memory is a preview feature that retains model-extracted customer detail, so it stays off unless explicitly enabled."
  type        = bool
  default     = false
}

variable "memory_update_delay_seconds" {
  description = "How long Foundry waits after a turn before extracting memories from it. Zero makes a preference recallable on the next turn, which is what a demonstration needs and what makes the behaviour observable. Raise it to batch extraction if turn volume ever justifies the delay."
  type        = number
  default     = 0

  validation {
    condition     = var.memory_update_delay_seconds >= 0
    error_message = "memory_update_delay_seconds must be zero or greater."
  }
}

variable "enable_agent_toolbox" {
  description = "Create the shared Foundry toolbox and let agents call its tools. Off by default so the baseline agents run without tool access."
  type        = bool
  default     = false
}

variable "webui_auth_client_id" {
  description = "Client ID of a hand-created Entra app registration used to sign users in to the Web UI. Empty disables Container Apps built-in authentication and leaves the Web UI public, which is the historical behaviour tracked by issue #40. The registration must list https://<webui-fqdn>/.auth/login/aad/callback as a redirect URI."
  type        = string
  default     = ""
}

variable "webui_auth_client_secret" {
  description = "Client secret for webui_auth_client_id. Supply it out of band (for example TF_VAR_webui_auth_client_secret) so it is never committed. Required whenever webui_auth_client_id is set."
  type        = string
  default     = ""
  sensitive   = true
}

variable "webui_auth_tenant_id" {
  description = "Entra tenant that issues Web UI sign-in tokens. Empty uses the tenant the infrastructure is deployed into, which is the single-tenant default. Set it when the app registration and its users live in a tenant the operator controls rather than the subscription's tenant; only sign-in moves, as managed identities and every Foundry data-plane call stay in the deployment tenant."
  type        = string
  default     = ""
}
