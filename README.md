# proj37 — Foundry Build vs Buy Agent (POC)

A serious proof-of-concept **.NET 10 Azure web app** that uses **Microsoft Foundry** prompt-agent
capabilities to:

1. **Ingest** received technical documents (SOWs, briefs, specs),
2. **Understand scope** (workload profile, scale, data sensitivity, in/out of scope),
3. **Define technical requirements** (compute, data, AI, security, networking, observability),
4. **Estimate Azure run cost** from the services/SKUs required, and
5. **Produce Excel calculation pages** (multi-sheet workbook with live formulas) for download.

It follows the architecture pattern of
[`qkfang/template-repo-agent`](https://github.com/qkfang/template-repo-agent)
(App Service + Foundry + Storage + monitoring, deployed via Bicep and GitHub Actions) and is grounded
in current Microsoft documentation for the App Service + Foundry Agent Service .NET tutorial, the
Foundry prompt-agent quickstart (Microsoft Agent Framework `AIProjectClient.AsAIAgent`), and the
Foundry file-search / vector-store guidance.

## How it works

```
 Upload docs ──> DocumentIngestionService ──> EstimationJobService ──> IEstimationEngine
                                                                         │
                                       ┌─────────────────────────────────┴───────────────┐
                                       │                                                   │
                              FoundryEstimationEngine                          OfflineEstimationEngine
                              (Microsoft Foundry prompt agent)                 (deterministic, signal-based)
                              SCOPE → REQUIREMENTS → SERVICE PLAN              SCOPE → REQUIREMENTS → COST
                                       │                                                   │
                                       └────────────► CostEstimate ◄──────────────────────┘
                                                          │
                                                ExcelReportGenerator (ClosedXML)
                                                          │
                                                  5-sheet .xlsx workbook
```

- **Live mode (`Foundry:ProjectEndpoint` configured):** a Microsoft Foundry prompt agent
  (Microsoft Agent Framework, in-process `AIProjectClient.AsAIAgent(...)`) performs the reasoning in
  three grounded JSON steps. The agent proposes the Azure service plan; the app prices it locally
  using `AzurePricingCatalog` so the **arithmetic is deterministic and auditable** (the model decides
  architecture, the app owns the math).
- **Offline mode (default):** a deterministic, signal-based engine derives scope/requirements/cost
  with zero external calls — so the POC always runs in CI, demos, and air-gapped environments. The
  Foundry engine also falls back to this automatically on any runtime error.

## Project layout

```

├─ apps/web/            # .NET 10 web app (Razor Pages UI + minimal API + OpenAPI)
│  ├─ Models/           # Estimation domain models + options
│  ├─ Services/         # Ingestion, pricing catalog, offline engine, Excel generator, job service
│  │  └─ Foundry/       # FoundryEstimationEngine (prompt-agent pipeline)
│  ├─ Pages/            # Razor Pages UI (Index, Error)
│  ├─ wwwroot/          # CSS + JS single-page front end
│  └─ Data/             # Bundled sample statement of work
├─ bicep/               # Infrastructure as code (modules + main + param)
├─ scripts/             # Local smoke test
├─ samples/             # Sample input documents
└─ docs/                # solution.md, task.md, todo.md
.github/workflows/
├─ proj37_foundry_cost_estimator_infra.yml    # provision Azure infra
└─ proj37_foundry_cost_estimator_deploy.yml   # build + deploy the app
```

## Run locally

```pwsh
cd apps/web
dotnet run
# open http://localhost:5217  (or the printed URL)
```

Then either upload `.md/.txt/.json/.csv/.docx` documents or click **Run sample brief**.
Download the generated Excel workbook from the result panel or the history table.

### Smoke test
```pwsh
pwsh scripts/smoke.ps1
```

## Enable the live Foundry agent

Set these (env vars or `appsettings`) and sign in with a credential that has the Foundry roles:

| Setting | Example |
| --- | --- |
| `Foundry__ProjectEndpoint` | `https://<name>.services.ai.azure.com/api/projects/<project>` |
| `Foundry__ModelDeploymentName` | `gpt-5.5` |

On Azure these are wired automatically by the Bicep `appservice` module, and the web app's managed
identity is granted **Cognitive Services User / OpenAI User**, **Storage Blob Data Contributor**, and
**Key Vault Secrets User**.

## Deploy (yoshi)

1. Run **`proj37_foundry_cost_estimator_infra`** (workflow_dispatch) → provisions everything.
2. Run **`proj37_foundry_cost_estimator_deploy`** (workflow_dispatch) → builds + zip-deploys the app,
   then smoke-tests `/api/health`.

Both use the repo's existing `AZURE_CREDENTIALS` secret and `rg-bvb` resource group.

## ⚠️ Pricing disclaimer

`AzurePricingCatalog` holds **reference list prices** (Pay-As-You-Go, Australia East, USD) captured for
a stable POC experience — **not a live feed and not a binding quote**. For production accuracy, swap it
for the [Azure Retail Prices API](https://prices.azure.com/api/retail/prices). The disclaimer is also
embedded in every generated workbook.
