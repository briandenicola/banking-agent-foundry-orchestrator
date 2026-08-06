variable "region" {
  description = "Azure region for the shared infrastructure."
  type        = string
  default     = "eastus2"
}

variable "enable_private_networking" {
  description = "When true, deploy Container Apps and PostgreSQL Flexible Server on private VNet paths and remove the broad Azure-services PostgreSQL firewall exception. Defaults false to preserve the quick-start lab path."
  type        = bool
  default     = false
}
