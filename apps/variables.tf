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
