from __future__ import annotations

from datetime import datetime, timezone
from typing import Any

from langchain_core.messages import HumanMessage, SystemMessage

from .schema import Complaint

_SYSTEM_PROMPT = (
    "You are a friendly housing complaint assistant. "
    "Your job is to guide residents to describe their complaint clearly. "
    "Keep replies short (1–2 sentences). "
    "Ask for the building name/ID, type of issue, and details if not provided. "
    "After a complaint is filed, acknowledge it warmly and ask if there is anything else."
)


def generate_prompt(llm: Any, history: list[dict[str, str]]) -> str:
    """Use the plain LLM to produce the next chatbot message based on conversation history."""
    messages = [SystemMessage(content=_SYSTEM_PROMPT)]
    for turn in history:
        if turn["role"] == "assistant":
            messages.append(SystemMessage(content=f"[You previously said]: {turn['content']}"))
        else:
            messages.append(HumanMessage(content=turn["content"]))
    response = llm.invoke(messages)
    return response.content.strip()


def bind_structured_output(llm: Any) -> Any:
    """Attach Pydantic schema extraction to a LangChain chat model."""
    return llm.with_structured_output(Complaint)


def insert_complaint(collection: Any, complaint: Complaint) -> dict[str, Any]:
    """Persist a validated complaint to MongoDB and return insert metadata."""
    document = complaint.model_dump()
    document["created_at"] = datetime.now(timezone.utc)
    inserted = collection.insert_one(document)
    return {
        "inserted_id": str(inserted.inserted_id),
        "complaint": document,
    }


def process_complaint(user_input: str, llm_with_structure: Any, collection: Any) -> dict[str, Any]:
    """Extract complaint entities with LangChain and persist to MongoDB."""
    complaint_data: Complaint = llm_with_structure.invoke(user_input)
    stored = insert_complaint(collection, complaint_data)
    return {
        "message": "Complaint filed successfully.",
        "inserted_id": stored["inserted_id"],
        "complaint": stored["complaint"],
    }


def group_complaints_by_building(collection: Any) -> list[dict[str, Any]]:
    """Aggregate complaint counts grouped by building id and name."""
    pipeline = [
        {
            "$group": {
                "_id": {
                    "building_id": "$building_id",
                    "building_name": "$building_name",
                },
                "count": {"$sum": 1},
            }
        },
        {"$sort": {"count": -1, "_id.building_id": 1}},
    ]
    return list(collection.aggregate(pipeline))

