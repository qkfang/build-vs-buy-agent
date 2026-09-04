# Solution Design — proj37 Foundry Cost Estimator POC

## Problem
A large enterprise receives technical documents and needs to: understand scope, define technical
requirements, estimate Azure run costs from the required services, and produce Excel calculation pages
— delivered as a .NET Azure web app using Microsoft Foundry prompt-agent capabilities.

## Architecture

| Layer | Choice | Why |
| --- | --- | --- |
| Web app | ASP.NET Core (.NET 10), Razor Pages UI + minimal API | Matches App Service `DOTNETCORE\|10.0`; minimal API gives a clean OpenAPI surface (usable as a Foundry OpenAPI tool per the App Service tutorial). |
| AI reasoning | Microsoft Foundry prompt agent via **Microsoft Agent Framework** (`AIProjectClient.AsAIAgent`) | Documented hosted/in-process pattern; full code control for a multi-step pipeline; fast local execution. |
| Document ingestion | `DocumentIngestionService` (text formats + DOCX via OpenXml) | Dependency-light, cross-platform; covers the common technical-doc formats. Live mode can additionally use Foundry vector store / file search. |
| Cost engine | `OfflineEstimationEngine` + `AzurePricingCatalog` | Deterministic, auditable arithmetic; always available; also prices the Foundry-proposed plan. |
| Excel output | `ExcelReportGenerator` (ClosedXML) | Produces a 5-sheet workbook with **live formulas** so reviewers can adjust inputs. |
| Persistence | File-based JSON + `.xlsx` under `/home/site/data` | Zero-dependency POC persistence; production-upgradeable to Blob (`Storage:AccountUrl`). |
| Infra | Bicep (App Service, Foundry, Storage, Key Vault, monitoring) + RBAC | Mirrors `template-repo-agent`; managed identity, keyless auth. |
| CI/CD | GitHub Actions (`infra` + `deploy`) | Matches repo conventions (`rg-bvb`, `AZURE_CREDENTIALS`, `azure/login@v1`). |

### Two-engine design
`IEstimationEngine` has two implementations selected at startup:
- **`FoundryEstimationEngine`** (when a `Foundry:ProjectEndpoint` is configured): runs three
  grounded prompt-agent calls returning JSON — **SCOPE**, **REQUIREMENTS**, and a concrete **SERVICE
  PLAN**. The plan (services/SKUs/quantities) is priced locally so totals are deterministic. Any
  failure (auth, quota, transient) transparently falls back to the offline engine and records why.
- **`OfflineEstimationEngine`** (default): `WorkloadSignals` extracts keyword signals from the
  ingested text (web/API/AI/file-search/SQL/NoSQL/Functions, scale band, PII/regulated) and maps them
  to a costed Azure architecture.

This guarantees the POC is always demonstrable while showcasing the real Foundry path.

## Estimation logic (offline engine)
- **Scale band** (Small / Medium / Large) is inferred from enterprise/scale keywords and explicit user
  counts (e.g. "120,000 monthly active users"). It drives App Service SKU + instance count, AI request
  volume, storage, telemetry, egress, and contingency (20% / 25%).
- **Data sensitivity** (internal / PII / regulated) is inferred from keywords and shapes security
  requirements.
- **Cost line items** are computed as `quantity × referenceUnitPrice` per service; the workbook
  re-expresses these as live Excel formulas.

> Heuristic limitation (documented honestly): the offline keyword matcher is not negation-aware (e.g.
> the literal token "PII" in "No PII" still flags sensitivity). This is intentionally conservative and
> is exactly the nuance the live Foundry agent handles better. The offline engine is a deterministic
> fallback, not the primary reasoning path.

## Excel workbook (deliverable)
Five sheets:
1. **Summary** — headline numbers (cross-sheet formulas to Cost Model), metadata, disclaimer.
2. **Cost Model** — per-service line items with `=Quantity*UnitPrice`, `SUM` subtotal, contingency,
   monthly total, and `=*12` annual total. AutoFilter + frozen header.
3. **Requirements** — derived technical requirements (ID, category, priority, rationale).
4. **Scope** — overview, goal, profile, scale, sensitivity, in/out of scope, assumptions.
5. **Documents** — ingested source documents with excerpts.

## Microsoft grounding (current docs)
- **Azure App Service + Foundry Agent Service (.NET) tutorial** — confirms the App Service `.NET`
  hosting pattern, the `AIProjectClient` / Agent Framework provider model injected as a service, and
  exposing app capabilities as an OpenAPI tool. The app calls `MapOpenApi()` and names operations.
- **Foundry prompt-agent quickstart (C#)** — confirms
  `new AIProjectClient(endpoint, DefaultAzureCredential()).AsAIAgent(model, instructions, name, tools)`
  and `agent.RunAsync(...)`; `Microsoft.Agents.AI.Foundry` + `Azure.AI.Projects` packages; env vars
  `AZURE_AI_PROJECT_ENDPOINT` / model deployment name.
- **Foundry file search / vector stores** — confirms the upload → vector store → `HostedFileSearchTool`
  flow, supported file types/encodings, RBAC (Storage Blob Data Contributor, Foundry Owner), and that
  file search has additional charges (captured as an AI Search line item).

## Security
- System-assigned managed identity on the web app; **keyless** Foundry access (`disableLocalAuth`).
- RBAC: Cognitive Services User + OpenAI User (Foundry), Storage Blob Data Contributor, Key Vault
  Secrets User.
- HTTPS-only, TLS 1.2+, FTPS disabled, no public blob access.

## Trade-offs / future work
- Replace `AzurePricingCatalog` with the Azure Retail Prices API for live pricing.
- Enable Foundry vector-store file search end-to-end in live mode (wiring is present; offline mode
  extracts text directly).
- Move job persistence from local files to Blob for multi-instance durability.
- Add negation-aware scope parsing in the offline engine (or rely on the live agent).
