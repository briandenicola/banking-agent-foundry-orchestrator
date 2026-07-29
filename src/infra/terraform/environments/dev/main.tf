module "banking_agent" {
  source = "../../modules"

  resource_group_name = "rg-banking-agent-dev"
  name_prefix         = "bankingagentdev"
  location            = "eastus"
}
