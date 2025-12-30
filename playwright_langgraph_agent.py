from __future__ import annotations

import asyncio
import json
import re
from datetime import datetime
from pathlib import Path
from typing import Optional

import streamlit as st
from dotenv import load_dotenv
from langchain.chat_models import init_chat_model
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_mcp_adapters.client import MultiServerMCPClient
from langgraph.graph import MessagesState, StateGraph, START
from langgraph.prebuilt import ToolNode, tools_condition

PLANNER_PROMPT = """You are an expert web test planner using MCP browser tools.
Follow this flow:
1) If planner_setup_page is available, call it once before other actions.
2) If a start URL is provided, navigate to it before executing steps.
3) Use browser_* tools to explore the UI and identify flows.
4) Save the plan using planner_save_plan if available.
Tool calls must match the tool schema exactly. If a field expects a list/array, pass a JSON array.
Return a complete test plan in markdown in one fenced code block."""

GENERATOR_PROMPT = """You are a Playwright test generator using MCP browser tools.
Follow this flow:
1) If generator_setup_page is available, call it once before other actions.
2) If a start URL is provided, navigate to it before executing steps.
3) Use browser_* tools to execute the request. Prefer stable locators and realistic flows.
4) Call generator_read_log if available, then use the log to craft the test.
Tool calls must match the tool schema exactly. If a field expects a list/array, pass a JSON array.
5) Return a single Playwright test file that matches the requested language and uses best practices.
Avoid deprecated APIs like networkidle waits.
Return only the test code in one fenced code block."""

HEALER_PROMPT = """You are the Playwright test healer using MCP test tools.
Follow this flow:
1) Run tests with test_run to identify failures. Limit runs to the provided test file when possible.
2) For each failure, use test_debug and browser_* tools to diagnose.
3) Update the provided test code to fix issues and improve reliability.
4) If no changes are needed, respond with NO_CHANGES.
Tool calls must match the tool schema exactly. If a field expects a list/array, pass a JSON array.
Avoid deprecated APIs like networkidle waits.
Return only the updated test code in one fenced code block or NO_CHANGES."""


def slugify(text: str) -> str:
    tokens = re.findall(r"[a-zA-Z0-9]+", text.lower())
    if not tokens:
        return "generated-test"
    return "-".join(tokens[:8])


def build_request_id(spec: str) -> str:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    return f"{slugify(spec)}-{timestamp}"


def default_output_path(spec: str, test_language: str, request_id: Optional[str]) -> Path:
    ext = "spec.ts" if test_language == "ts" else "spec.py"
    name = request_id or slugify(spec)
    return Path("tests") / f"{name}.{ext}"


def plan_output_path(request_id: str) -> Path:
    return Path("specs") / f"{request_id}-plan.md"


def extract_fenced_block(text: str) -> str:
    fenced = re.search(r"```(?:[a-zA-Z0-9_+-]+)?\n(.*?)```", text, re.S)
    if fenced:
        return fenced.group(1).strip()
    return text.strip()


def load_mcp_server_config(config_path: Path, server_name: str) -> dict:
    if config_path.exists():
        data = json.loads(config_path.read_text(encoding="utf-8"))
        servers = data.get("servers", {})
        server = servers.get(server_name)
        if server:
            return server
    return {
        "type": "stdio",
        "command": "npx",
        "args": ["playwright", "run-test-mcp-server"],
    }


def normalize_mcp_config(server_config: dict) -> dict:
    server_type = server_config.get("type", "stdio")
    if server_type == "stdio":
        return {
            "command": server_config.get("command", "npx"),
            "args": server_config.get("args", []),
            "transport": "stdio",
        }
    if server_type in {"streamable_http", "http"}:
        return {
            "url": server_config["url"],
            "transport": "streamable_http",
        }
    raise ValueError(f"Unsupported MCP server type: {server_type}")


def build_plan_prompt(
    spec: str,
    start_url: Optional[str],
    plan_path: Path,
) -> str:
    url_text = start_url or "None provided"
    return (
        "Create a comprehensive test plan for the request below.\n"
        f"Start URL: {url_text}\n"
        f"Plan path: {plan_path.as_posix()}\n\n"
        f"Request:\n{spec}"
    )


def build_generation_prompt(
    spec: str,
    start_url: Optional[str],
    plan_path: Path,
    plan_text: str,
    output_path: Path,
    test_language: str,
) -> str:
    url_text = start_url or "None provided"
    return (
        "Create a Playwright test from the request and plan below.\n"
        f"Test language: {test_language}\n"
        f"Start URL: {url_text}\n"
        f"Plan path: {plan_path.as_posix()}\n"
        f"Output path: {output_path.as_posix()}\n\n"
        f"Request:\n{spec}\n\n"
        f"Plan:\n{plan_text}"
    )


def build_heal_prompt(
    spec: str,
    plan_path: Path,
    plan_text: str,
    test_path: Path,
    test_code: str,
) -> str:
    return (
        "Heal the Playwright test using the plan and current code below.\n"
        f"Plan path: {plan_path.as_posix()}\n"
        f"Test path: {test_path.as_posix()}\n\n"
        f"Request:\n{spec}\n\n"
        f"Plan:\n{plan_text}\n\n"
        f"Current test code:\n{test_code}"
    )


def build_graph(model, tools):
    model_with_tools = model.bind_tools(tools)

    async def call_model(state: MessagesState):
        response = await model_with_tools.ainvoke(state["messages"])
        return {"messages": response}

    builder = StateGraph(MessagesState)
    builder.add_node("call_model", call_model)
    builder.add_node("tools", ToolNode(tools, handle_tool_error=True))
    builder.add_edge(START, "call_model")
    builder.add_conditional_edges("call_model", tools_condition)
    builder.add_edge("tools", "call_model")
    return builder.compile()


async def close_client(client: MultiServerMCPClient) -> None:
    for name in ("aclose", "close"):
        closer = getattr(client, name, None)
        if callable(closer):
            result = closer()
            if asyncio.iscoroutine(result):
                await result
            return


async def run_workflow(
    spec: str,
    start_url: Optional[str],
    output_path: Optional[Path],
    test_language: str,
    model_name: str,
    mcp_config: Path,
    mcp_server: str,
) -> tuple[Path, str, Path, str, bool]:
    spec = spec.strip()
    request_id = build_request_id(spec)
    plan_path = plan_output_path(request_id)
    output_path = output_path or default_output_path(spec, test_language, request_id)

    plan_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    server_config = load_mcp_server_config(mcp_config, mcp_server)
    client = MultiServerMCPClient({mcp_server: normalize_mcp_config(server_config)})

    try:
        tools = await client.get_tools()
        model = init_chat_model(model_name, temperature=0)
        graph = build_graph(model, tools)

        plan_prompt = build_plan_prompt(spec, start_url, plan_path)
        plan_messages = [SystemMessage(content=PLANNER_PROMPT), HumanMessage(content=plan_prompt)]
        plan_result = await graph.ainvoke({"messages": plan_messages})
        plan_text = extract_fenced_block(plan_result["messages"][-1].content)
        if not plan_text:
            plan_text = f"# Plan\n\nRequest:\n{spec}\n"
        plan_path.write_text(plan_text + "\n", encoding="utf-8")

        generation_prompt = build_generation_prompt(
            spec,
            start_url,
            plan_path,
            plan_text,
            output_path,
            test_language,
        )
        generation_messages = [
            SystemMessage(content=GENERATOR_PROMPT),
            HumanMessage(content=generation_prompt),
        ]
        generation_result = await graph.ainvoke({"messages": generation_messages})
    finally:
        await close_client(client)

    test_code = extract_fenced_block(generation_result["messages"][-1].content)
    if not test_code:
        raise ValueError("No test code returned by generator.")

    output_path.write_text(test_code + "\n", encoding="utf-8")
    healed = False

    server_config = load_mcp_server_config(mcp_config, mcp_server)
    client = MultiServerMCPClient({mcp_server: normalize_mcp_config(server_config)})
    try:
        tools = await client.get_tools()
        model = init_chat_model(model_name, temperature=0)
        graph = build_graph(model, tools)

        heal_prompt = build_heal_prompt(spec, plan_path, plan_text, output_path, test_code)
        heal_messages = [SystemMessage(content=HEALER_PROMPT), HumanMessage(content=heal_prompt)]
        heal_result = await graph.ainvoke({"messages": heal_messages})
    finally:
        await close_client(client)

    heal_text = heal_result["messages"][-1].content.strip()
    if heal_text.upper() != "NO_CHANGES":
        healed_code = extract_fenced_block(heal_text)
        if healed_code:
            if healed_code != test_code:
                healed = True
            test_code = healed_code
            output_path.write_text(test_code + "\n", encoding="utf-8")

    return plan_path, plan_text, output_path, test_code, healed


def run_workflow_sync(
    spec: str,
    start_url: Optional[str],
    output_path: Optional[str],
    test_language: str,
    model_name: str,
    mcp_config: str,
    mcp_server: str,
) -> tuple[Path, str, Path, str, bool]:
    path = Path(output_path) if output_path else None
    return asyncio.run(
        run_workflow(
            spec=spec,
            start_url=start_url,
            output_path=path,
            test_language=test_language,
            model_name=model_name,
            mcp_config=Path(mcp_config),
            mcp_server=mcp_server,
        )
    )


def init_state() -> None:
    if "messages" not in st.session_state:
        st.session_state.messages = []
    if "model_name" not in st.session_state:
        st.session_state.model_name = "openai:gpt-4.1"
    if "test_language" not in st.session_state:
        st.session_state.test_language = "ts"
    if "start_url" not in st.session_state:
        st.session_state.start_url = ""
    if "output_path" not in st.session_state:
        st.session_state.output_path = ""
    if "mcp_config" not in st.session_state:
        st.session_state.mcp_config = str(Path(".vscode") / "mcp.json")
    if "mcp_server" not in st.session_state:
        st.session_state.mcp_server = "playwright-test"


def render_sidebar() -> None:
    st.sidebar.header("Settings")
    st.session_state.model_name = st.sidebar.text_input(
        "Model",
        value=st.session_state.model_name,
    )
    st.session_state.test_language = st.sidebar.selectbox(
        "Test language",
        options=["ts", "py"],
        index=0 if st.session_state.test_language == "ts" else 1,
    )
    st.session_state.start_url = st.sidebar.text_input(
        "Start URL (optional)",
        value=st.session_state.start_url,
    )
    st.session_state.output_path = st.sidebar.text_input(
        "Output path (optional)",
        value=st.session_state.output_path,
        help="Leave blank to auto-generate a path from the prompt.",
    )
    st.session_state.mcp_config = st.sidebar.text_input(
        "MCP config path",
        value=st.session_state.mcp_config,
    )
    st.session_state.mcp_server = st.sidebar.text_input(
        "MCP server name",
        value=st.session_state.mcp_server,
    )
    st.sidebar.caption("Uses Playwright MCP from your .vscode/mcp.json by default.")


def render_messages() -> None:
    for message in st.session_state.messages:
        with st.chat_message(message["role"]):
            st.markdown(message["content"])
            if "plan_text" in message:
                st.code(message["plan_text"], language="markdown")
            if "plan_path" in message:
                st.caption(f"Plan saved to {message['plan_path']}")
            if "test_code" in message:
                language = "ts" if message.get("test_language") == "ts" else "python"
                st.code(message["test_code"], language=language)
            if "test_path" in message:
                st.caption(f"Test saved to {message['test_path']}")
            if "healed" in message:
                label = "Auto-heal applied." if message["healed"] else "Auto-heal made no changes."
                st.caption(label)


def main() -> None:
    load_dotenv()
    st.set_page_config(page_title="Playwright LangGraph Agent")
    st.title("Playwright LangGraph Agent")
    st.caption("Plan, generate, and auto-heal Playwright tests from natural language using MCP tools.")

    init_state()
    render_sidebar()
    render_messages()

    prompt = st.chat_input("Describe the test you want to generate")
    if not prompt:
        return

    st.session_state.messages.append({"role": "user", "content": prompt})
    with st.chat_message("user"):
        st.markdown(prompt)
    with st.chat_message("assistant"):
        with st.spinner("Planning, generating, and healing..."):
            try:
                plan_path, plan_text, test_path, test_code, healed = run_workflow_sync(
                    spec=prompt,
                    start_url=st.session_state.start_url or None,
                    output_path=st.session_state.output_path or None,
                    test_language=st.session_state.test_language,
                    model_name=st.session_state.model_name,
                    mcp_config=st.session_state.mcp_config,
                    mcp_server=st.session_state.mcp_server,
                )
            except Exception as exc:
                st.error("Generation failed.")
                st.exception(exc)
                return

        st.markdown("Here is the plan:")
        st.code(plan_text, language="markdown")
        st.caption(f"Plan saved to {plan_path}")

        st.markdown("Here is the generated test:")
        language = "ts" if st.session_state.test_language == "ts" else "python"
        st.code(test_code, language=language)
        st.caption(f"Test saved to {test_path}")
        st.caption("Auto-heal applied." if healed else "Auto-heal made no changes.")

    st.session_state.messages.append(
        {
            "role": "assistant",
            "content": "Planned, generated, and healed the Playwright test.",
            "plan_text": plan_text,
            "plan_path": str(plan_path),
            "test_code": test_code,
            "test_path": str(test_path),
            "test_language": st.session_state.test_language,
            "healed": healed,
        }
    )


if __name__ == "__main__":
    main()
