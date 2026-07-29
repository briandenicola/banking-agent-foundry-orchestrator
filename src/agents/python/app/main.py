from fastapi import FastAPI

app = FastAPI(title="Banking Agent Service")

@app.get("/health")
def health():
    return {"status": "ok"}

@app.post("/reason")
def reason(payload: dict):
    return {
        "trace_id": payload.get("trace_id", "unknown"),
        "status": "ok",
        "intent": "transaction_explanation",
        "requires_approval": False,
        "reason": "Stubbed reasoning agent response"
    }

@app.post("/plan")
def plan(payload: dict):
    return {
        "trace_id": payload.get("trace_id", "unknown"),
        "status": "ok",
        "decision": "informational",
        "requires_approval": False,
        "next_step": "respond_to_user"
    }
