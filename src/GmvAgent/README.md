# GMV Evidence Agent

Dockerized ASP.NET Core app for asking questions about Greenwich Millennium Village using MongoDB document chunks and Claude.

## Retrieval

- Uses MongoDB Atlas Vector Search index `embedding_vector_cosine_1024` when `VOYAGE_API_KEY` is set.
- Falls back to MongoDB text search when no Voyage key is configured.
- Sends retrieved chunks to Claude and asks it to cite source ids like `[source_id#chunk_index]`.

Anthropic does not provide its own embedding model; its embedding guide recommends Voyage AI. The existing MongoDB vectors are 1024-dimensional, matching Voyage defaults.

## Local Run

From this folder:

```powershell
$env:MONGODB_CONNECTION_STRING="mongodb+srv://..."
$env:ANTHROPIC_API_KEY="sk-ant-..."
$env:VOYAGE_API_KEY="pa-..."
dotnet run
```

Open the URL printed by `dotnet run`.

For development only, the app can also read `APiKeys.json` from a parent folder. Docker does not copy that file.

## Docker

Copy `.env.example` to `.env`, fill in values, then:

```powershell
docker compose up --build
```

Open <http://localhost:8080>.

## Required MongoDB Shape

Database: `agent_memory`

Collection: `documents`

Expected fields:

- `building_id`
- `source_id`
- `source_type`
- `chunk_text`
- `chunk_index`
- `embedding` as 1024 floats
- `metadata.filename`
- `metadata.page`

Recommended indexes already created:

- `embedding_vector_cosine_1024`
- `building_source_chunk_lookup`
- `source_chunk_order`
- `metadata_filename_lookup`
- `source_type_source_lookup`
- `chunk_text_keyword_search`

## Image Credits

- Renaissance Walk image from Wikimedia Commons by Manxruler, CC BY-SA / GFDL.
- Renaissance Walk image from Geograph by David Anstiss, CC BY-SA 2.0.
- Archive overview image from local `Findings-gmv` generated research archive.
