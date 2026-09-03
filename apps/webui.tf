resource "azurerm_container_app" "webui" {
  name                         = local.webui_app_name
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this["webui"].id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = azurerm_user_assigned_identity.this["webui"].id
  }

  # Read by name, by Easy Auth or by the application's own OpenID Connect
  # handler depending on which is in use. Absent unless a registration was
  # supplied; see webui-auth.tf and webui-delegation.tf.
  dynamic "secret" {
    for_each = local.webui_auth_enabled ? [1] : []
    content {
      name  = local.webui_auth_secret_name
      value = var.webui_auth_client_secret
    }
  }

  ingress {
    allow_insecure_connections = false
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "webui"
      image  = local.webui_image
      cpu    = 0.5
      memory = "1Gi"

      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 5
        timeout                 = 3
        failure_count_threshold = 30
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        initial_delay           = 10
        interval_seconds        = 15
        timeout                 = 3
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        interval_seconds        = 10
        timeout                 = 6
        failure_count_threshold = 3
        success_count_threshold = 1
      }

      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.this["webui"].client_id
      }

      env {
        name = "ORCHESTRATOR_API_BASE_URL"
        # Environment-internal FQDN. See local.orchestrator_internal_fqdn for why
        # this is constructed instead of read from the orchestrator's computed
        # ingress attribute.
        value = local.orchestrator_internal_url
      }

      env {
        name  = "ORCHESTRATOR_TOKEN_SCOPE"
        value = local.orchestrator_token_scope
      }

      env {
        name  = "DATA_PROTECTION_KEYS_PATH"
        value = "/tmp/banking-agent-data-protection"
      }

      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = data.azurerm_application_insights.this.connection_string
      }

      env {
        name = "WEBUI_AUTH_ENABLED"
        # Tells the application whether to trust the Easy Auth principal
        # headers. Those headers are only trustworthy when the platform is
        # actually terminating authentication in front of the container; if the
        # app trusted them unconditionally, any caller could forge an identity
        # and read another customer's memories. False when delegation is on,
        # because then no platform layer is injecting them.
        value = tostring(local.webui_auth_enabled && !local.user_delegation_enabled)
      }

      # Delegated user sign-in. Every value below is empty and inert unless
      # enable_user_delegation is set; the application reads
      # USER_DELEGATION_ENABLED first and ignores the rest when it is false.
      env {
        name  = "USER_DELEGATION_ENABLED"
        value = tostring(local.user_delegation_enabled)
      }

      env {
        name  = "ORCHESTRATOR_API_SCOPE"
        value = local.orchestrator_api_scope
      }

      dynamic "env" {
        for_each = local.user_delegation_enabled ? [1] : []
        content {
          name  = "WEBUI_AUTH_CLIENT_ID"
          value = var.webui_auth_client_id
        }
      }

      dynamic "env" {
        for_each = local.user_delegation_enabled ? [1] : []
        content {
          name  = "WEBUI_AUTH_TENANT_ID"
          value = local.webui_auth_tenant_id
        }
      }

      # The Web UI is a confidential client and must prove it is the registered
      # application, so it needs the same secret Easy Auth uses. Passed by
      # reference so the value never appears in the container app's environment
      # definition.
      dynamic "env" {
        for_each = local.user_delegation_enabled ? [1] : []
        content {
          name        = "WEBUI_AUTH_CLIENT_SECRET"
          secret_name = local.webui_auth_secret_name
        }
      }
    }
  }

  lifecycle {
    precondition {
      condition     = var.webui_auth_client_id == "" || var.webui_auth_client_secret != ""
      error_message = "webui_auth_client_id is set but webui_auth_client_secret is empty. Easy Auth would redirect users to a login it cannot complete, so the Web UI would be unusable rather than merely unauthenticated. Supply the secret, for example with TF_VAR_webui_auth_client_secret."
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azuread_app_role_assignment.webui_workflow_invoke,
    # ORCHESTRATOR_API_BASE_URL is now a constructed string rather than a
    # reference to the orchestrator resource, so state the ordering explicitly:
    # the orchestrator must be on internal ingress before the Web UI is pointed
    # at its internal FQDN.
    azurerm_container_app.orchestrator,
  ]
}
