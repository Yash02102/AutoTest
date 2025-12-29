# AutoTest

## LangGraph Playwright Agent (Python)

This repo includes a Streamlit chat UI that generates Playwright tests from natural language requests using LangChain,
LangGraph, and the Playwright MCP server.

### Prereqs
- Python 3.10+
- Node + Playwright (for `npx playwright run-test-mcp-server`)
- An LLM API key for the model you choose (for example `OPENAI_API_KEY`)

### Install
```bash
pip install -r requirements.txt
```

### Run
```bash
streamlit run playwright_langgraph_agent.py
```

Use the chat input to enter a test request, then configure the model, start URL, output path, and MCP settings in the
sidebar. Each request generates a new plan in `specs/` and a test in `tests/`, then runs an auto-heal pass if needed.

The agent reads MCP settings from `.vscode/mcp.json` by default.

### Environment
The app loads environment variables from a local `.env` file at startup. Example:
```bash
OPENAI_API_KEY=sk-...
```
