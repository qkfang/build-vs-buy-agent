# Project: Adventure Works Customer Portal Modernization

## Statement of Work (Technical Brief)

### Business context
Adventure Works runs an ageing on-premises customer portal where B2B customers place orders, track
shipments, and download invoices. The business wants to modernise it as a cloud-hosted web application
with an API back end and an AI assistant that answers customer questions about their orders and the
product catalogue, grounded in product documentation and order data.

### Functional requirements
- A customer-facing web front end (responsive) for orders, shipment tracking, and invoice downloads.
- An HTTP API (REST/OpenAPI) for the portal and for partner integrations.
- Generative AI assistant with file search / retrieval-augmented generation over product manuals,
  FAQs, and policy documents.
- A GPT-class prompt agent that answers customer questions and can summarise an order's status.
- A relational database for orders, shipments, and invoices.
- Serverless background jobs for invoice generation and shipment-status webhooks.

### Non-functional requirements
- Expected scale: approximately 40,000 monthly active business users, steady peak load of low-hundreds of
  requests per second. Production, business-important system with good availability.
- Data sensitivity: contains customer PII and commercial/financial data; encryption and least-privilege
  access required.
- Observability: end-to-end tracing, metrics, and centralized logging.
- Security: managed identity, secrets vault, HTTPS only, private storage.

### Constraints & assumptions
- Primary Azure region is Australia East.
- Favour managed PaaS; size for steady growth over the next two years.
- Single region for this phase; design so HA can be added later.

### Out of scope (for this phase)
- Payment processing (handled by an existing gateway).
- Native mobile apps.
- Data-warehouse / BI reporting (separate initiative).

### Off-the-shelf software product cost comparison
The following is an indicative price list for buying a commercial off-the-shelf (COTS) /
SaaS customer-portal + AI-assistant product instead of building the solution ourselves.
Use it as the "buy" baseline to compare against the estimated "build" cost. All figures are
indicative AUD for a production B2B portal of ~40,000 monthly active business users.

| Cost category | Type | Indicative cost (AUD) | Notes |
|---|---|---|---|
| Product licensing / subscription | Recurring (annual) | $312,000 / yr | Customer-portal platform + AI assistant module at ~40k MAU |
| Onboarding & implementation (SI partner) | One-time | $180,000 | Discovery, portal configuration, migration planning, go-live |
| Setup & configuration | One-time | $62,000 | Branding, catalogue, workflows, RBAC, security hardening |
| Data migration (orders, shipments, invoices) | One-time | $90,000 | Migrate from ageing on-prem portal |
| Integration (partner API, SSO, gateway, webhooks) | One-time | $105,000 | REST/OpenAPI, SSO, existing payment gateway, shipment webhooks |
| Content ingestion (manuals, FAQs, policies) | One-time | $30,000 | Corpus load and indexing for the AI assistant |
| Annual support & maintenance | Recurring (annual) | $62,400 | Vendor support (20% of licence), availability SLA |
| Training & change management | One-time | $35,000 | Customer-service and admin enablement |
| **Year 1 total (one-time + first-year recurring)** | — | **≈ $876,400** | One-time $502,000 + first-year recurring $374,400 |
| **Ongoing annual run cost (year 2+)** | Recurring | **≈ $374,400 / yr** | Subscription + support |
