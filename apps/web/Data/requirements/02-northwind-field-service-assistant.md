# Project: Northwind Field Service Assistant

## Statement of Work (Technical Brief)

### Business context
Northwind Logistics runs a national fleet of field technicians who service refrigeration and HVAC
equipment. Technicians currently phone a help desk to look up manuals, warranty terms, and repair
procedures. The business wants a lightweight web + mobile-friendly assistant that answers technician
questions grounded in the equipment manuals and past work orders, so first-time-fix rates improve.

### Functional requirements
- A responsive web front end usable on tablets in the field.
- An HTTP API so the dispatch system can pre-load the relevant equipment context for a job.
- Generative AI assistant with file search / retrieval-augmented generation over a corpus of equipment
  manuals, service bulletins, and historical work orders.
- A GPT-class prompt agent that drafts step-by-step repair guidance and a parts list.
- Store work-order summaries and feedback in a NoSQL document store for flexible, high-write throughput.

### Non-functional requirements
- Expected scale: around 5,000 technicians, roughly 30,000 assistant queries per month, modest peak load
  (tens of requests per second).
- Data sensitivity: internal operational data; limited PII (technician identifiers). No regulated financial data.
- Observability: request tracing and basic usage metrics.
- Security: managed identity and a secrets vault; HTTPS only.

### Constraints & assumptions
- Primary Azure region is Australia East.
- Favour managed PaaS and consumption-based services to keep run cost low.
- This is a production pilot for one business unit, with room to grow.

### Out of scope (for this phase)
- Offline mode / on-device inference.
- Integration with third-party parts-ordering systems.
- Native iOS/Android apps (responsive web only for now).

### Off-the-shelf software product cost comparison
The following is an indicative price list for buying a commercial off-the-shelf (COTS) /
SaaS field-service knowledge-assistant product instead of building the solution ourselves.
Use it as the "buy" baseline to compare against the estimated "build" cost. All figures are
indicative AUD for a production pilot of ~5,000 technicians and ~30,000 queries per month.

| Cost category | Type | Indicative cost (AUD) | Notes |
|---|---|---|---|
| Product licensing / subscription | Recurring (annual) | $138,000 / yr | Per-technician subscription (~5,000 seats), mid tier |
| Onboarding & implementation | One-time | $70,000 | Guided onboarding, environment setup, go-live |
| Setup & configuration | One-time | $28,000 | Knowledge base config, roles, branding |
| Content ingestion (manuals, bulletins, work orders) | One-time | $32,000 | Corpus load and indexing for retrieval |
| Integration (dispatch system API, SSO) | One-time | $40,000 | Pre-load job context + single sign-on |
| Annual support & maintenance | Recurring (annual) | $27,600 | Vendor standard support (20% of licence) |
| Training & enablement | One-time | $18,000 | Technician and dispatcher training |
| **Year 1 total (one-time + first-year recurring)** | — | **≈ $353,600** | One-time $188,000 + first-year recurring $165,600 |
| **Ongoing annual run cost (year 2+)** | Recurring | **≈ $165,600 / yr** | Subscription + support |
