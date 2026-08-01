from __future__ import annotations

from enum import Enum
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, computed_field, model_validator

CONTRACT_VERSION = "1.0"


class AgentName(str, Enum):
    WORKFLOW_PLANNING = "workflow-planning"
    TRANSACTION_EXPLANATION = "transaction-explanation"
    SUSPICIOUS_ACTIVITY = "suspicious-activity"
    DISPUTE_PLANNING = "dispute-planning"


class AgentRequest(BaseModel):
    model_config = ConfigDict(extra="allow")

    contract_version: Literal["1.0"] = CONTRACT_VERSION
    message: str = Field(min_length=1)
    trace_id: str = "unknown"
    workflow_id: str | None = None
    tool_name: str | None = None
    agent_name: str | None = None
    input: dict[str, Any] = Field(default_factory=dict)
    metadata: dict[str, Any] = Field(default_factory=dict)
    context: dict[str, Any] = Field(default_factory=dict)

    @model_validator(mode="after")
    def _normalize_context(self) -> AgentRequest:
        """Top-level context is authoritative. When it is absent, promote
        input.context so callers using the legacy envelope still obtain a
        populated specialist context without code changes elsewhere."""
        if "context" not in self.model_fields_set:
            legacy = self.input.get("context")
            if isinstance(legacy, dict):
                self.context = legacy
        return self

    @computed_field
    @property
    def specialist_context(self) -> dict[str, Any]:
        """Return the resolved specialist context (top-level authoritative)."""
        return self.context


class AgentResult(BaseModel):
    agent: AgentName
    status: Literal["ok", "error"] = "ok"
    trace_id: str
    contract_version: str = CONTRACT_VERSION
    execution_mode: Literal["model", "fallback"] = "fallback"
    intent: str
    summary: str
    risk_level: Literal["low", "medium", "high"]
    requires_approval: bool
    recommended_action: str
    next_step: str
    selected_agent: AgentName | None = None
    evidence: list[str] = Field(default_factory=list)
