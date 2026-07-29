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
