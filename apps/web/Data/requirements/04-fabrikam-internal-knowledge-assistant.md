# Project: Fabrikam Internal Knowledge Assistant

## Statement of Work (Technical Brief)

### Business context
Fabrikam wants a small internal "ask the handbook" assistant for its 400 head-office staff. Employees
should be able to ask questions about HR policy, IT procedures, and travel rules and get a grounded answer
with a citation to the source policy document. This is an internal productivity pilot with a low budget.

### Functional requirements
- A simple internal web front end (single sign-on assumed) for asking questions.
- Generative AI assistant using file search / retrieval-augmented generation over a small set of policy PDFs
  and wiki exports.
- A GPT-class prompt agent that answers in plain language and cites the source section.
- No relational database required; conversation history can be kept in lightweight blob storage.

### Non-functional requirements
- Expected scale: ~400 staff, a few thousand questions per month, very low peak load.
- Data sensitivity: internal-only business documents; minimal PII.
- Observability: basic usage metrics and error logging.
- Security: managed identity, secrets vault, HTTPS only.

### Constraints & assumptions
- Primary Azure region is Australia East.
- This is a low-cost internal pilot; favour the smallest viable SKUs and consumption pricing.
- Not a mission-critical production system — single region, no HA required.

### Out of scope (for this phase)
- Public/customer access.
- Integration with the HR system of record.
- Multi-language support.

### Off-the-shelf software product cost comparison
The following is an indicative price list for buying a commercial off-the-shelf (COTS) /
SaaS internal "ask the handbook" knowledge-assistant product instead of building the solution
ourselves. Use it as the "buy" baseline to compare against the estimated "build" cost. All
figures are indicative AUD for a low-budget internal pilot of ~400 staff and a few thousand
questions per month.

| Cost category | Type | Indicative cost (AUD) | Notes |
|---|---|---|---|
| Product licensing / subscription | Recurring (annual) | $19,200 / yr | Per-user subscription (~400 seats), entry tier |
| Onboarding & implementation | One-time | $8,000 | Self-serve / lightly-assisted onboarding |
| Setup & configuration | One-time | $4,000 | SSO connection, branding, roles |
| Content ingestion (policy PDFs, wiki) | One-time | $3,500 | Small corpus load and indexing |
| Annual support & maintenance | Recurring (annual) | $3,840 | Vendor standard support (20% of licence) |
| Training & enablement | One-time | $2,500 | Brief admin + staff enablement |
| **Year 1 total (one-time + first-year recurring)** | — | **≈ $41,040** | One-time $18,000 + first-year recurring $23,040 |
| **Ongoing annual run cost (year 2+)** | Recurring | **≈ $23,040 / yr** | Subscription + support |
