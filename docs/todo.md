# Todo — proj37 Foundry Cost Estimator POC

## Done (toad)
- [x] Scaffold project structure (apps/bicep/docs/scripts/samples).
- [x] Domain models (scope, requirements, cost, documents, options).
- [x] Document ingestion service (text formats + DOCX).
- [x] Azure pricing reference catalog (documented, disclaimed).
- [x] Deterministic offline estimation engine + workload-signal extraction.
- [x] Foundry prompt-agent engine (`AIProjectClient.AsAIAgent`, 3-step JSON pipeline, safe fallback).
- [x] Multi-sheet Excel generator with live formulas (ClosedXML).
- [x] Job orchestration + file-based persistence.
- [x] Razor Pages UI (upload, results tabs, history) + minimal API + OpenAPI.
- [x] Bicep modules + main + param (App Service, Foundry, Storage, Key Vault, monitoring, RBAC).
- [x] GitHub Actions: infra + deploy workflows (repo conventions).
- [x] Local verification: build, publish, sample run, multi-file upload, unsupported-file rejection,
      Excel formula validation.
- [x] Docs: README, solution, task, todo. Smoke script.

## QA (toadette)
- [ ] Build + run; exercise `/api/health`, sample estimation, upload, workbook download.
- [ ] Open the workbook; confirm 5 sheets and that totals recalc when a quantity/unit price changes.
- [ ] Review scope/requirements/cost for a couple of different input documents (scale sensitivity).
- [ ] Confirm graceful failure on unsupported-only upload (HTTP 422 + message).
- [ ] Lint Bicep (`az bicep build`) and review RBAC least-privilege.

## Deploy (yoshi) — mandatory after QA PASS
- [ ] Run `proj37_foundry_cost_estimator_infra` (workflow_dispatch).
- [ ] Run `proj37_foundry_cost_estimator_deploy` (workflow_dispatch).
- [ ] Verify `/api/health` returns 200 and the site loads; run a sample estimation in the cloud.
- [ ] (Optional) Set `foundryEnabled=true` and confirm the live agent path + model quota.

## Possible enhancements
- [ ] Swap pricing catalog for the Azure Retail Prices API.
- [ ] End-to-end Foundry vector-store file search in live mode.
- [ ] Blob-backed persistence for multi-instance durability.
- [ ] Negation-aware scope parsing in the offline engine.
