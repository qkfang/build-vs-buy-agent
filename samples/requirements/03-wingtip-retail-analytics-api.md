# Project: Wingtip Retail Analytics API

## Statement of Work (Technical Brief)

### Business context
Wingtip Traders wants a back-end analytics API that ingests point-of-sale events from 1,200 stores
and exposes near-real-time sales and inventory metrics to internal dashboards. There is no customer-facing
UI in this phase — the deliverable is a robust, well-instrumented HTTP API and the data plumbing behind it.

### Functional requirements
- A documented HTTP API (REST/OpenAPI) returning aggregated sales, basket, and inventory metrics.
- High-volume event ingestion: serverless functions process point-of-sale events and write to storage.
- A relational database for curated, queryable aggregates that power the dashboards.
- Blob storage for raw event archives and exported reports.
- This is a classic data and integration workload focused on throughput, not analytics narratives.

### Non-functional requirements
- Expected scale: ~1,200 stores emitting events, tens of millions of function executions per month,
  sustained moderate API traffic from internal dashboards.
- Data sensitivity: internal commercial data; no PII beyond store/operator identifiers.
- Observability: detailed metrics, tracing, and ingestion lag monitoring are essential.
- Security: managed identity, secrets vault, HTTPS only, private storage.

### Constraints & assumptions
- Primary Azure region is Australia East.
- Optimise for throughput and cost-efficiency at high event volume (consumption-based compute preferred).
- Production system feeding finance and operations dashboards.

### Out of scope (for this phase)
- Any web/UI front end (API only).
- Predictive forecasting (a later phase).
- Cross-region replication.

### Off-the-shelf software product cost comparison
The following is an indicative price list for buying a commercial off-the-shelf (COTS) /
SaaS retail analytics + event-ingestion platform instead of building the solution ourselves.
Use it as the "buy" baseline to compare against the estimated "build" cost. All figures are
indicative AUD for a production API workload ingesting events from ~1,200 stores (tens of
millions of events per month).

| Cost category | Type | Indicative cost (AUD) | Notes |
|---|---|---|---|
| Platform licensing / subscription | Recurring (annual) | $264,000 / yr | Volume-based ingestion + API tier at high event throughput |
| Onboarding & implementation | One-time | $120,000 | Data-model design, environment provisioning, go-live |
| Setup & configuration | One-time | $55,000 | Pipelines, aggregates, API surface, metrics config |
| Data migration & historical backfill | One-time | $48,000 | Seed curated aggregates and raw archive import |
| Integration (POS event sources, dashboards) | One-time | $85,000 | 1,200-store event feeds + internal dashboard connectors |
| Annual support & maintenance | Recurring (annual) | $52,800 | Vendor support (20% of licence), ingestion-lag SLA |
| Training & enablement | One-time | $22,000 | Data/ops team onboarding |
| **Year 1 total (one-time + first-year recurring)** | — | **≈ $646,800** | One-time $330,000 + first-year recurring $316,800 |
| **Ongoing annual run cost (year 2+)** | Recurring | **≈ $316,800 / yr** | Subscription + support |
