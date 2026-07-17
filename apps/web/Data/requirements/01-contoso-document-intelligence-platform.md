# Project: Contoso Document Intelligence Platform

## Statement of Work (Technical Brief)

### Business context
Contoso Corporate Services receives thousands of inbound business documents (PDFs, scanned
forms, emails) every day across procurement, HR, and facilities. The business wants an
enterprise web application that lets operations staff upload documents and get an AI-generated
summary, extracted entities, and a recommended next action. The goal is to reduce manual triage
time and improve consistency across the shared-services team.

### Functional requirements
- A secure web portal (front end) where operations staff sign in and upload one or more documents.
- An HTTP API (REST/OpenAPI) so the existing line-of-business system can submit documents programmatically.
- Generative AI document understanding: ingest the uploaded documents, build a knowledge base, and answer
  staff questions grounded in the documents (retrieval-augmented generation / file search).
- Use a large language model (GPT-class) prompt agent to extract scope, categorise the document, and draft a
  recommended next action. Advanced reasoning is required for complex, multi-document cases.
- Persist structured document records and audit history in a relational database.
- Background processing for OCR and notification webhooks should scale independently of the web tier.

### Non-functional requirements
- Expected scale: approximately 120,000 monthly active users across the enterprise, with peak load of
  several hundred requests per second during business hours. This is a mission-critical, production system
  that requires high availability.
- Data sensitivity: documents contain personally identifiable information (PII) and financial data.
  The platform must comply with the company's regulatory and SOC 2 obligations, including encryption,
  least-privilege access, and full auditability.
- Observability: end-to-end tracing, metrics, and centralized logging are mandatory.
- Security: no secrets in source or config; use managed identity and a secrets vault.

### Constraints & assumptions
- Primary Azure region is Australia East.
- The solution should favour managed PaaS services to minimise operations overhead.
- This is a production deployment (not a throwaway prototype), so size for resilience and growth.

### Out of scope (for this phase)
- Training a custom model from scratch (use pre-built / Foundry models).
- Mobile native apps.
- Migration of historical documents older than 7 years.

### Off-the-shelf software product cost comparison
The following is an indicative price list for buying a commercial off-the-shelf (COTS) /
SaaS enterprise document-intelligence product instead of building the solution ourselves.
Use it as the "buy" baseline to compare against the estimated "build" cost. All figures are
indicative AUD for an enterprise deployment at ~120,000 monthly active users, mission-critical tier.

| Cost category | Type | Indicative cost (AUD) | Notes |
|---|---|---|---|
| Product licensing / subscription | Recurring (annual) | $520,000 / yr | Enterprise tier, per-seat + document-volume pricing at ~120k MAU |
| Onboarding & implementation (SI partner) | One-time | $280,000 | Discovery, solution design, environment provisioning, go-live |
| Setup & configuration | One-time | $95,000 | Workflow, taxonomy, RBAC, tenancy and security hardening |
| Data migration & ingestion | One-time | $110,000 | Historical document load (7-year corpus), OCR reprocessing |
| Integration (LOB API, SSO, webhooks) | One-time | $130,000 | REST integration to line-of-business system + Entra ID SSO |
| Compliance & security accreditation | One-time | $75,000 | SOC 2 / PII controls validation, penetration test |
| Annual support & maintenance | Recurring (annual) | $104,000 | Vendor premium support (20% of licence), SLA-backed |
| Training & change management | One-time | $45,000 | Operations staff enablement, admin training |
| Premium/priority SLA uplift | Recurring (annual) | $60,000 | 99.9% availability, mission-critical response times |
| **Year 1 total (one-time + first-year recurring)** | — | **≈ $1,419,000** | One-time $735,000 + first-year recurring $684,000 |
| **Ongoing annual run cost (year 2+)** | Recurring | **≈ $684,000 / yr** | Subscription + support + SLA uplift |
