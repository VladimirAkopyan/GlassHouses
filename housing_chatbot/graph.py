"""LangGraph-powered complaint chatbot graph."""
from __future__ import annotations

from typing import Annotated, Any

from langchain_core.messages import SystemMessage, ToolMessage
from langchain_core.tools import tool
from langgraph.checkpoint.memory import MemorySaver
from langgraph.graph import END, START, StateGraph
from langgraph.graph.message import add_messages
from typing_extensions import TypedDict

from .schema import Complaint
from .service import insert_complaint

SYSTEM_PROMPT = (
    "You are a friendly housing complaint assistant for a residential estate. "
    "Your job is to help residents file complaints by collecting: "
    "(1) building name or ID, (2) type of issue (e.g. plumbing, noise, heating), "
    "(3) a clear description. "
    "Ask follow-up questions if any information is missing. "
    "Once you have all three pieces of information, call the `file_complaint_now` tool "
    "with building_id, building_name, category, and description. "
    "After the complaint is filed, confirm warmly and ask if there is anything else. "
    "Keep conversational replies concise (1–2 sentences)."
)


class State(TypedDict):
    messages: Annotated[list, add_messages]


def build_graph(llm: Any, collection: Any):
    """
    Build and compile the LangGraph complaint chatbot.

    Nodes
    -----
    chatbot          — Conversational LLM; calls `file_complaint_now` tool when ready.
    handle_complaint — Validates tool args as a Complaint, saves to MongoDB,
                       and returns a ToolMessage result.

    Flow
    ----
    START → chatbot → (tool signal) → handle_complaint → chatbot
                    → (no tool)     → END
    """

    @tool
    def file_complaint_now(
        building_id: str,
        building_name: str,
        category: str,
        description: str,
    ) -> str:  # noqa: D401
        """
        File a complaint once you know the building ID, building name, category,
        and description.
        """
        return "ok"

    llm_with_tools = llm.bind_tools([file_complaint_now], parallel_tool_calls=False)

    # ------------------------------------------------------------------ nodes

    def chatbot(state: State) -> dict:
        system = SystemMessage(content=SYSTEM_PROMPT)
        response = llm_with_tools.invoke([system] + state["messages"])
        return {"messages": [response]}

    def handle_complaint(state: State) -> dict:
        """Persist complaints from tool calls and emit matching tool results."""
        new_messages: list[ToolMessage] = []
        last_ai = state["messages"][-1]
        for tc in getattr(last_ai, "tool_calls", []):
            if tc.get("name") != "file_complaint_now":
                continue
            try:
                complaint = Complaint.model_validate(tc.get("args", {}))
                result = insert_complaint(collection, complaint)
                new_messages.append(
                    ToolMessage(
                        content=(
                            "Complaint filed successfully. "
                            f"Reference ID: {result['inserted_id']} | "
                            f"Building: {complaint.building_name} ({complaint.building_id}) | "
                            f"Category: {complaint.category}."
                        ),
                        tool_call_id=tc["id"],
                    )
                )
            except Exception as exc:
                new_messages.append(
                    ToolMessage(
                        content=(
                            "Complaint could not be filed because the details were incomplete "
                            f"or invalid: {exc}"
                        ),
                        tool_call_id=tc["id"],
                        status="error",
                    )
                )
        return {"messages": new_messages}

    # --------------------------------------------------------- routing logic

    def route_after_chatbot(state: State) -> str:
        last = state["messages"][-1]
        if getattr(last, "tool_calls", None):
            return "handle_complaint"
        return END

    # ------------------------------------------------------------------ graph

    graph = StateGraph(State)
    graph.add_node("chatbot", chatbot)
    graph.add_node("handle_complaint", handle_complaint)

    graph.add_edge(START, "chatbot")
    graph.add_conditional_edges("chatbot", route_after_chatbot)
    graph.add_edge("handle_complaint", "chatbot")

    return graph.compile(checkpointer=MemorySaver())
