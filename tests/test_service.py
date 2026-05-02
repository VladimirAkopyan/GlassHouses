from unittest.mock import Mock

import mongomock

from housing_chatbot.schema import Complaint
from housing_chatbot.service import (
    bind_structured_output,
    group_complaints_by_building,
    insert_complaint,
    process_complaint,
)


class FakeStructuredLLM:
    def __init__(self, payload: Complaint):
        self.payload = payload

    def invoke(self, _user_input: str) -> Complaint:
        return self.payload


def test_bind_structured_output_uses_langchain_api() -> None:
    llm = Mock()
    llm.with_structured_output.return_value = "structured"

    output = bind_structured_output(llm)

    llm.with_structured_output.assert_called_once_with(Complaint)
    assert output == "structured"


def test_process_complaint_inserts_document() -> None:
    mongo = mongomock.MongoClient()
    collection = mongo["housing_db"]["complaints"]
    structured_llm = FakeStructuredLLM(
        Complaint(
            building_id="GMV-302",
            building_name="Building 302",
            category="noise",
            description="Persistent late-night noise from neighboring unit.",
        )
    )

    result = process_complaint("Noise complaint", structured_llm, collection)

    assert result["message"] == "Complaint filed successfully."
    stored = collection.find_one({"_id": mongomock.ObjectId(result["inserted_id"])})
    assert stored is not None
    assert stored["building_id"] == "GMV-302"
    assert stored["building_name"] == "Building 302"
    assert stored["category"] == "noise"
    assert "created_at" in stored


def test_insert_complaint_inserts_validated_document() -> None:
    mongo = mongomock.MongoClient()
    collection = mongo["housing_db"]["complaints"]

    stored = insert_complaint(
        collection,
        Complaint(
            building_id="GMV-304",
            building_name="Building 304",
            category="heating",
            description="The radiators are cold all evening.",
        ),
    )

    assert stored["inserted_id"]
    persisted = collection.find_one({"_id": mongomock.ObjectId(stored["inserted_id"])})
    assert persisted is not None
    assert persisted["building_id"] == "GMV-304"
    assert persisted["building_name"] == "Building 304"
    assert persisted["category"] == "heating"
    assert persisted["description"] == "The radiators are cold all evening."
    assert "created_at" in persisted


def test_group_complaints_by_building() -> None:
    mongo = mongomock.MongoClient()
    collection = mongo["housing_db"]["complaints"]
    collection.insert_many(
        [
            {
                "building_id": "GMV-302",
                "building_name": "Building 302",
                "category": "noise",
                "description": "A",
            },
            {
                "building_id": "GMV-302",
                "building_name": "Building 302",
                "category": "plumbing",
                "description": "B",
            },
            {
                "building_id": "GMV-303",
                "building_name": "Building 303",
                "category": "heating",
                "description": "C",
            },
        ]
    )

    rows = group_complaints_by_building(collection)

    assert rows[0]["_id"]["building_id"] == "GMV-302"
    assert rows[0]["count"] == 2
    assert rows[1]["_id"]["building_id"] == "GMV-303"
    assert rows[1]["count"] == 1

