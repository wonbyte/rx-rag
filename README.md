# rxrag

A RAG chatbot over public FDA drug labels. .NET 10, Azure OpenAI, Azure AI Search, React.

Flow: openFDA -> chunk -> embed -> Azure AI Search (hybrid) -> grounded prompt -> answer with citations.

## Run locally

    cp .env.example .env   # fill in your endpoints
    set -a; source .env; set +a
    az login

    dotnet test
    dotnet run --project src/RxRag.Ingest
    dotnet run --project src/RxRag.Api --urls http://localhost:5080
    cd web && npm run dev   # open http://localhost:5173

    dotnet run --project src/RxRag.Evals

## Design notes

- No API keys. Entra ID everywhere (DefaultAzureCredential, workload identity in AKS).
- Hybrid search (keyword + vector, fused with RRF) with contextual chunk headers.
- Strict grounding prompt, fixed refusal sentence, citations checked in code.
- Evals gate CI: refusals must all pass; answer quality has thresholds.
