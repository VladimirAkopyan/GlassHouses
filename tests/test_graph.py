import mongomock
from langchain_core.messages import AIMessage, HumanMessage, ToolMessage

from housing_chatbot.graph import build_graph


class FakeBoundLLM:
    def __init__(self) -> None:
        self.invocations: list[list] = []

    def invoke(self, messages: list) -> AIMessage:
        self.invocations.append(messages)
        if any(isinstance(message, ToolMessage) for message in messages):
            return AIMessage(
                content="Your complaint has been logged successfully. Is there anything else I can help you with?"
            )
        return AIMessage(
            content="",
            tool_calls=[
                {
                    "name": "file_complaint_now",
                    "args": {
                        "building_id": "GMV-302",
                        "building_name": "Building 302",
                        "category": "noise",
                        "description": "Persistent late-night noise from the neighbouring flat.",
                    },
                    "id": "call-1",
                    "type": "tool_call",
                }
            ],
        )


class FakeLLM:
    def __init__(self) -> None:
        self.bound = FakeBoundLLM()
        self.bind_tools_kwargs: dict | None = None

    def bind_tools(self, _tools: list, **kwargs: dict) -> FakeBoundLLM:
        self.bind_tools_kwargs = kwargs
        return self.bound


class InvalidToolCallLLM(FakeLLM):
    def __init__(self) -> None:
        super().__init__()
        self.bound = FakeBoundLLM()

        def invalid_invoke(messages: list) -> AIMessage:
            self.bound.invocations.append(messages)
            if any(isinstance(message, ToolMessage) for message in messages):
                return AIMessage(content="Please share the missing building details and I can try again.")
            return AIMessage(
                content="",
                tool_calls=[
                    {
                        "name": "file_complaint_now",
                        "args": {
                            "building_name": "Building 302",
                            "category": "noise",
                        },
                        "id": "call-invalid",
                        "type": "tool_call",
                    }
                ],
            )

        self.bound.invoke = invalid_invoke


def test_graph_returns_tool_result_before_follow_up_assistant_message() -> None:
    mongo = mongomock.MongoClient()
    collection = mongo["housing_db"]["complaints"]
    llm = FakeLLM()
    app = build_graph(llm, collection)

    result = app.invoke(
        {"messages": [HumanMessage(content="I need to report late-night noise in Building 302.")]},
        config={"configurable": {"thread_id": "test-thread"}},
    )

    messages = result["messages"]
    assistant_tool_index = next(
        index for index, message in enumerate(messages) if getattr(message, "tool_calls", None)
    )

    assert llm.bind_tools_kwargs == {"parallel_tool_calls": False}
    assert isinstance(messages[assistant_tool_index + 1], ToolMessage)
    assert isinstance(messages[-1], AIMessage)
    assert "logged successfully" in messages[-1].content
    assert collection.count_documents({}) == 1
    assert len(llm.bound.invocations) == 2
    assert not any(isinstance(message, ToolMessage) for message in llm.bound.invocations[0])
    assert any(isinstance(message, ToolMessage) for message in llm.bound.invocations[1])


def test_graph_returns_error_tool_result_for_invalid_tool_args() -> None:
    mongo = mongomock.MongoClient()
    collection = mongo["housing_db"]["complaints"]
    llm = InvalidToolCallLLM()
    app = build_graph(llm, collection)

    result = app.invoke(
        {"messages": [HumanMessage(content="I want to complain about noise.")]},
        config={"configurable": {"thread_id": "invalid-thread"}},
    )

    tool_messages = [message for message in result["messages"] if isinstance(message, ToolMessage)]

    assert len(tool_messages) == 1
    assert tool_messages[0].status == "error"
    assert "could not be filed" in str(tool_messages[0].content)
    assert collection.count_documents({}) == 0
    assert isinstance(result["messages"][-1], AIMessage)


