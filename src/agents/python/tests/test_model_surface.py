"""Which API surface the hosted agents call Foundry on.

A specialist that cannot reach its model falls back to canned text rather
than raising, so a misconfigured surface does not announce itself. These
tests pin the surface at the point it is chosen.
"""
from __future__ import annotations

import pytest

from app import model as model_module


class TestUseResponsesApi:
    def test_defaults_to_responses(self, monkeypatch):
        # The surface used to be decided by LangChain's default rather than by
        # this repository. Unset must now mean Responses, not Chat Completions.
        monkeypatch.delenv(model_module.USE_RESPONSES_API_ENV_VAR, raising=False)
        assert model_module._use_responses_api() is True

    @pytest.mark.parametrize("value", ["false", "FALSE", " no ", "0", "off", "Off"])
    def test_explicit_negatives_opt_out(self, monkeypatch, value):
        monkeypatch.setenv(model_module.USE_RESPONSES_API_ENV_VAR, value)
        assert model_module._use_responses_api() is False

    @pytest.mark.parametrize("value", ["true", "1", "yes", "on", "anything else"])
    def test_anything_else_stays_on_responses(self, monkeypatch, value):
        # Opt-out, not opt-in: an unrecognised value must not silently revert
        # the surface the way an unrecognised ALLOW_FALLBACK disables fallback.
        monkeypatch.setenv(model_module.USE_RESPONSES_API_ENV_VAR, value)
        assert model_module._use_responses_api() is True

    def test_empty_string_is_not_an_opt_out(self, monkeypatch):
        monkeypatch.setenv(model_module.USE_RESPONSES_API_ENV_VAR, "   ")
        assert model_module._use_responses_api() is True


class TestModelConstruction:
    def _build(self, monkeypatch, **env):
        monkeypatch.setattr(model_module, "DefaultAzureCredential", lambda *a, **k: object())
        monkeypatch.setattr(
            model_module, "get_bearer_token_provider", lambda *a, **k: (lambda: "token")
        )
        for key in (
            model_module.PROJECT_ENDPOINT_ENV_VAR,
            model_module.LEGACY_PROJECT_ENDPOINT_ENV_VAR,
            "AZURE_OPENAI_ENDPOINT",
            model_module.USE_RESPONSES_API_ENV_VAR,
        ):
            monkeypatch.delenv(key, raising=False)
        for key, value in env.items():
            monkeypatch.setenv(key, value)
        return model_module._model()

    def test_foundry_client_uses_responses_by_default(self, monkeypatch):
        built = self._build(
            monkeypatch,
            **{model_module.PROJECT_ENDPOINT_ENV_VAR: "https://example.services.ai.azure.com/api/projects/p"},
        )
        assert built.use_responses_api is True

    def test_foundry_client_honours_the_opt_out(self, monkeypatch):
        built = self._build(
            monkeypatch,
            **{
                model_module.PROJECT_ENDPOINT_ENV_VAR: "https://example.services.ai.azure.com/api/projects/p",
                model_module.USE_RESPONSES_API_ENV_VAR: "false",
            },
        )
        assert built.use_responses_api is False

    def test_base_url_still_targets_the_project_v1_surface(self, monkeypatch):
        # Both surfaces answer under /openai/v1/; Responses must not change it.
        built = self._build(
            monkeypatch,
            **{model_module.PROJECT_ENDPOINT_ENV_VAR: "https://example.services.ai.azure.com/api/projects/p/"},
        )
        assert str(built.root_client.base_url).rstrip("/").endswith("/api/projects/p/openai/v1")

    def test_local_azure_openai_branch_is_left_on_chat_completions(self, monkeypatch):
        # Deliberately unchanged: that surface needs a different api-version
        # and is not exercised by the deployed stack. Unset reads as None
        # rather than False, which is LangChain's "use the default surface".
        built = self._build(monkeypatch, AZURE_OPENAI_ENDPOINT="https://example.openai.azure.com/")
        assert not built.use_responses_api

    def test_no_endpoint_still_yields_no_model(self, monkeypatch):
        assert self._build(monkeypatch) is None
