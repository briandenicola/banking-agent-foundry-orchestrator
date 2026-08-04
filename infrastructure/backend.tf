# Local state is the default for this demo. The azurerm remote backend remains
# available for future production-style state hosting, but the default workflow
# should remain simple and self-contained.
terraform {
  backend "local" {}
}
