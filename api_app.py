from __future__ import annotations

from pathlib import Path
from typing import Optional

from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from playwright_langgraph_agent import run_workflow_sync


class GenerateRequest(BaseModel):
    spec: str = Field(..., description="Natural language request for the test")
    start_url: Optional[str] = Field(default=None, description="Optional start URL for planning")
    output_path: Optional[str] = Field(default=None, description="Optional test output path")
    test_language: str = Field(default="ts", description="Test language: ts or py")
    model_name: str = Field(default="openai:gpt-4.1", description="LLM identifier")
    mcp_config: str = Field(default=str(Path(".vscode") / "mcp.json"), description="MCP config path")
    mcp_server: str = Field(default="playwright-test", description="MCP server name")


class GenerateResponse(BaseModel):
    plan_path: str
    plan_text: str
    test_path: str
    test_code: str
    healed: bool


app = FastAPI(title="Playwright LangGraph Agent API")


@app.on_event("startup")
def load_env() -> None:
    load_dotenv()


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/generate", response_model=GenerateResponse)
def generate_tests(payload: GenerateRequest) -> GenerateResponse:
    try:
        plan_path, plan_text, test_path, test_code, healed = run_workflow_sync(
            spec=payload.spec,
            start_url=payload.start_url or None,
            output_path=payload.output_path or None,
            test_language=payload.test_language,
            model_name=payload.model_name,
            mcp_config=payload.mcp_config,
            mcp_server=payload.mcp_server,
        )
    except Exception as exc:  # noqa: BLE001 - return API-friendly error
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    return GenerateResponse(
        plan_path=str(plan_path),
        plan_text=plan_text,
        test_path=str(test_path),
        test_code=test_code,
        healed=healed,
    )
