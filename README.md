# GlassHouses

Housing complaint chatbot using LangChain structured output, LangGraph, and MongoDB.

## What this example does

- Multi-turn chatbot powered by **Anthropic Claude** via LangGraph.
- `chatbot` node gathers complaint details conversationally; signals readiness with a tool call.
- `handle_complaint` node extracts a validated Pydantic schema via `with_structured_output` and inserts into MongoDB.
- Aggregates complaint counts by building with a MongoDB aggregation pipeline.

## Files

- `housing_chatbot/schema.py` — Complaint Pydantic schema.
- `housing_chatbot/graph.py` — LangGraph graph (`chatbot` → `handle_complaint`).
- `housing_chatbot/service.py` — Grouping/aggregation helpers.
- `housing_chatbot/main.py` — CLI runner.
- `tests/test_service.py` — Unit tests.

## Quick start

1. Install dependencies:

    ```powershell
    python -m pip install -r requirements.txt
    ```

2. Set environment variables:

    ```powershell
    $env:MONGODB_DB        = "housing_db"          # optional, default shown
    $env:MONGODB_COLLECTION= "complaints"          # optional, default shown
    $env:ANTHROPIC_MODEL   = "claude-sonnet-4-0"  # optional
    ```

    Set the Claude API key directly in `housing_chatbot/main.py` via the
    `ANTHROPIC_API_KEY` constant.

3. Run the interactive chatbot:

    ```powershell
    python -m housing_chatbot
    ```

4. Single-shot complaint (non-interactive):

    ```powershell
    python -m housing_chatbot "Building 302 has loud noise every night from 11pm."
    ```

5. Show grouped complaint summary:

    ```powershell
    python -m housing_chatbot --summary
    ```

## Run tests

```powershell
python -m pytest -q
```
