# Task — proj37 Foundry Cost Estimator POC

**project_id:** proj37
**project_code:** proj37_foundry_cost_estimator_poc
**owner (build):** toad · **QA:** toadette · **deploy:** yoshi

## Goal
Build a serious .NET Azure web app POC that uses Microsoft Foundry prompt-agent capabilities to ingest
technical documents, understand scope, define technical requirements, estimate Azure run costs, and
produce downloadable Excel calculation pages. Pattern after `qkfang/template-repo-agent`. Deploy to
Azure after QA PASS.

## Acceptance criteria
- [x] .NET web app builds and runs locally (`dotnet run`) with 0 warnings / 0 errors.
- [x] Ingests technical documents (md/txt/json/csv/docx) and rejects unsupported files gracefully.
- [x] Uses Microsoft Foundry prompt-agent pattern (`AIProjectClient.AsAIAgent`) for scope →
      requirements → service plan, with a deterministic offline fallback that always works.
- [x] Produces a multi-sheet Excel workbook with **live formulas** (downloadable via UI + API).
- [x] Cost estimate is grounded in a documented Azure pricing reference catalog with a clear
      "not a quote" disclaimer.
- [x] Bicep infra (App Service + Foundry + Storage + Key Vault + monitoring + RBAC) compiles clean.
- [x] GitHub Actions workflows (infra + deploy) follow repo conventions and are valid YAML.
- [x] Docs (README, solution, task, todo) present.
- [ ] QA PASS by toadette.
- [ ] Azure deployment verified by yoshi.

## Endpoints
- `GET  /api/health` — engine mode + liveness.
- `POST /api/estimations` — multipart upload → estimate → result JSON.
- `POST /api/estimations/sample` — run the bundled sample brief (demo/CI).
- `GET  /api/estimations` — list jobs.
- `GET  /api/estimations/{id}` — full result.
- `GET  /api/estimations/{id}/workbook` — download the `.xlsx`.
- `GET  /openapi/v1.json` — OpenAPI document (Foundry OpenAPI-tool ready).
