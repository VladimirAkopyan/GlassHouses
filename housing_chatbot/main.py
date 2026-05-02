from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

from pydantic import SecretStr

# Hardcoded provider API key used by the chatbot runtime.
ANTHROPIC_API_KEY = SecretStr("")
DEFAULT_MODEL = "claude-haiku-4-5-20251001"

# LangSmith tracing is opt-in. If you have valid LANGCHAIN_* env vars set,
# LangChain/LangGraph will pick them up automatically.

from langchain_core.messages import HumanMessage
from langchain_anthropic import ChatAnthropic
from pymongo import MongoClient

try:
    from .graph import build_graph
    from .service import group_complaints_by_building
except ImportError:
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    from housing_chatbot.graph import build_graph
    from housing_chatbot.service import group_complaints_by_building


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Housing complaint chatbot")
    parser.add_argument("input", nargs="?", help="Natural language complaint text (single-shot mode)")
    parser.add_argument(
        "--summary",
        action="store_true",
        help="Show grouped complaint counts by building",
    )
    parser.add_argument(
        "--db",
        default=os.getenv("MONGODB_DB", "housing_db"),
        help="MongoDB database name",
    )
    parser.add_argument(
        "--collection",
        default=os.getenv("MONGODB_COLLECTION", "complaints"),
        help="MongoDB collection name",
    )
    parser.add_argument(
        "--model",
        default=os.getenv("ANTHROPIC_MODEL", DEFAULT_MODEL),
        help="Claude model to use",
    )
    return parser


def main() -> None:
    args = build_parser().parse_args()

    model_hint = f"Try --model {DEFAULT_MODEL} or set ANTHROPIC_MODEL."

    client = MongoClient("mongodb+srv://chatbot:chatbot@cluster0.zjwaeg.mongodb.net/?appName=Cluster0")
    collection = client[args.db][args.collection]

    # --- Summary mode ---
    if args.summary:
        for row in group_complaints_by_building(collection):
            building = row["_id"]
            print(f"{building['building_id']} ({building['building_name']}): {row['count']}")
        return

    llm = ChatAnthropic(
        model_name=args.model,
        api_key=ANTHROPIC_API_KEY,
        timeout=None,
        stop=None,
    )
    app = build_graph(llm, collection)
    config = {"configurable": {"thread_id": "session-1"}}

    # --- Single-shot mode ---
    if args.input:
        try:
            result = app.invoke({"messages": [HumanMessage(content=args.input)]}, config=config)
        except Exception as exc:
            message = str(exc)
            if "not_found_error" in message or "model:" in message:
                raise SystemExit(
                    f"Claude model '{args.model}' was not found. {model_hint}"
                ) from exc
            raise
        print(f"Bot: {result['messages'][-1].content}")
        return

    # --- Interactive mode (LangGraph loop) ---
    print("Housing Complaint Chatbot  |  type 'quit' or 'exit' to stop.\n")

    # Bot speaks first
    try:
        greeting = app.invoke({"messages": [HumanMessage(content="Hello")]}, config=config)
    except Exception as exc:
        message = str(exc)
        if "not_found_error" in message or "model:" in message:
            raise SystemExit(
                f"Claude model '{args.model}' was not found. {model_hint}"
            ) from exc
        raise
    print(f"Bot: {greeting['messages'][-1].content}\n")

    while True:
        try:
            user_input = input("You: ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nGoodbye!")
            break

        if not user_input:
            continue
        if user_input.lower() in {"quit", "exit"}:
            print("Bot: Thanks for reaching out. Goodbye!")
            break

        try:
            result = app.invoke({"messages": [HumanMessage(content=user_input)]}, config=config)
        except Exception as exc:
            message = str(exc)
            if "not_found_error" in message or "model:" in message:
                print(f"Bot: Claude model '{args.model}' was not found. {model_hint}\n")
                break
            raise
        print(f"Bot: {result['messages'][-1].content}\n")


if __name__ == "__main__":
    main()

