# Local state is the default for this demo. The app stack uses the same
# simple local-state workflow as the infrastructure stack.
terraform {
  backend "local" {}
}
