from __future__ import annotations

from enum import Enum
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field


class AgentName(str, Enum):
    WORKFLOW_PLANNING = "workflow-planning"
    TRANSACTION_EXPLANATION = "transaction-explanation"
    SUSPICIOUS_ACTIVITY = "suspicious-activity"
    DISPUTE_PLANNING = "dispute-planning"


class AgentRequest(BaseModel):
    model_config = ConfigDict(extra="allow")

    message: str = Field(min_length=1)
    trace_id: str = "unknown"
    workflow_id: str | None = None
    input: dict[str, Any] = Field(default_factory=dict)
    metadata: dict[str, Any] = Field(default_factory=dict)
    context: dict[str, Any] = Field(default_factory=dict)


class AgentResult(BaseModel):
    agent: AgentName
    status: Literal["ok", "error"] = "ok"
    trace_id: str
    intent: str
    summary: str
    risk_level: Literal["low", "medium", "high"]
    requires_approval: bool
    recommended_action: str
    next_step: str
    selected_agent: AgentName | None = None
    evidence: list[str] = Field(default_factory=list)
