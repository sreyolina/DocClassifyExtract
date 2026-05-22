# DocClassifyExtract — Azure Functions Document Processing Pipeline

## Overview

An Azure Functions v4 (.NET 8 isolated worker) application that classifies uploaded documents, validates the classification with GPT-based confidence scoring, extracts structured fields for high-confidence known document types, and stores successful extraction results in SQL Server.

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Azure Blob Storage                                  │
│                      Container: "genpact"                                   │
│                                                                             │
│   📄 Upload PDF ──► Blob Trigger fires automatically                       │
└──────────────────────────────┬──────────────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                  Azure Function: ClassifyAndExtract                         │
│          (Blob Trigger on "genpact/incoming-documents/{name}")             │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 1: Document ID Derivation                                      │    │
│  │   • Parse filename → extract DocumentId    │    │
│  │   • Generate unique JobId (GUID)                                    │    │
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 2: Document Classification + Validation                        │    │
│  │   • Call Azure Content Understanding Classifier API                 │    │
│  │   • Classifier: "doc_classifier_cre_cni_valuation_                 │    │
│  │     confidence_score_other"                                         │    │
│  │   • API Version: 2025-11-01                                         │    │
│  │   • Categories: CRE / Valuation / CNI / Other                       │    │
│  │                                                                     │    │
│  │   Returns segments, each with:                                      │    │
│  │     ─ Category                                                      │    │
│  │     ─ Page range (startPage – endPage)                              │    │
│  │     ─ Classifier confidence                                          │    │
│  │                                                                     │    │
│  │   For CRE / Valuation / CNI only:                                   │    │
│  │     ─ Take markdown excerpt from first 20% of relevant pages        │    │
│  │     ─ Send excerpt to GPT-4.1                                       │    │
│  │     ─ Compute confidence score                                      │    │
│  │                                                                     │    │
│  │   If category = Other OR confidence score < 70%:                    │    │
│  │     ─ Log human intervention required                               │    │
│  │     ─ Stop processing                                                │    │
│  │     ─ Do not extract, save to DB, or assign SME                     │    │
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 3: Generate SAS URL                                            │    │
│  │   • Create 24-hour read-only SAS token for PDF citation links       │    │
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 4: Per-Segment Field Extraction (loop)                         │    │
│  │                                                                     │    │
│  │   For each high-confidence classified segment:                      │    │
│  │                                                                     │    │
│  │   ┌──────────────────────────────────────────────────────────┐      │    │
│  │   │ 4a. Map category → analyzer                              │      │    │
│  │   │     CRE        → cre_loan_analyzer        (9 fields)    │      │    │
│  │   │     Valuation   → appraisal_report_analyzer (22 fields)  │      │    │
│  │   │     CNI         → cni_agreement_analyzer    (9 fields)   │      │    │
│  │   └──────────────────────┬───────────────────────────────────┘      │    │
│  │                          │                                          │    │
│  │                          ▼                                          │    │
│  │   ┌──────────────────────────────────────────────────────────┐      │    │
│  │   │ 4b. Call Extraction API                                  │      │    │
│  │   │     POST /{analyzer}:analyze → poll for result           │      │    │
│  │   │     Returns: fields, markdown, pages, citations          │      │    │
│  │   └──────────────────────┬───────────────────────────────────┘      │    │
│  │                          │                                          │    │
│  │                          ▼                                          │    │
│  │   ┌──────────────────────────────────────────────────────────┐      │    │
│  │   │ 4c. Load Schema Field Methods                            │      │    │
│  │   │     Read analyzer-schemas/*.json                         │      │    │
│  │   │     Map each field → "extract" or "generate"             │      │    │
│  │   └──────────────────────┬───────────────────────────────────┘      │    │
│  │                          │                                          │    │
│  │                          ▼                                          │    │
│  │   ┌──────────────────────────────────────────────────────────┐      │    │
│  │   │ 4d. DocumentFieldExtractor processes each field:         │      │    │
│  │   │                                                          │      │    │
│  │   │   ┌─ EXTRACT fields ─────────────────────────────────┐   │      │    │
│  │   │   │  • Value from API "valueString"                  │   │      │    │
│  │   │   │  • Confidence from API                           │   │      │    │
│  │   │   │  • Citation: page, bounding box, span, PDF link  │   │      │    │
│  │   │   │  • Source parsed by CitationService               │   │      │    │
│  │   │   └──────────────────────────────────────────────────┘   │      │    │
│  │   │                                                          │      │    │
│  │   │   ┌─ GENERATE fields ────────────────────────────────┐   │      │    │
│  │   │   │  • Value from API "valueString"                  │   │      │    │
│  │   │   │  • Confidence computed via markdown text match    │   │      │    │
│  │   │   │  • Citation found by fuzzy search in document    │   │      │    │
│  │   │   │  • ConfidenceReason explains scoring              │   │      │    │
│  │   │   └──────────────────────────────────────────────────┘   │      │    │
│  │   │                                                          │      │    │
│  │   │   • FeatureRefService resolves FeatureId from DB         │      │    │
│  │   │   • Missing values marked as "H-I-T-L"                  │      │    │
│  │   └──────────────────────────────────────────────────────────┘      │    │
│  │                                                                     │    │
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 5: Determine Job Status                                        │    │
│  │   • Only reached for known categories with confidence ≥ 70%         │    │
│  │   • "Successful"          — all fields have values                  │    │
│  │   • "Partially Successful" — some fields have values                │    │
│  │   • "Failed"              — no fields have values                   │    │
│  │   • "No Fields Extracted" — zero fields returned                    │    │
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 6: Save to SQL Server                                          │    │
│  │   • Only reached for known categories with confidence ≥ 70%         │    │
│  │   • MERGE INTO dbo.FeatureData (upsert per DocumentId + FeatureId)  │    │
│  │   • INSERT/UPDATE dbo.JobDetails (job tracking)                     │    │
│  │   • Auto-create FeatureRef entries for new field names              │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Component Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                   Program.cs (DI Setup)                      │
│  Registers: BlobServiceClient, HttpClient, all services      │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│            ClassifyAndExtractFunction.cs                      │
│            (Orchestrator — Blob Trigger)                      │
│                                                               │
│  Dependencies:                                                │
│  ├── IContentUnderstandingService  (classify + extract)       │
│  ├── IDocumentFieldExtractor       (field processing)         │
│  ├── IDatabaseService              (SQL persistence)          │
│  └── BlobServiceClient             (SAS URL generation)       │
└──────┬──────────┬──────────┬──────────┬─────────────────────┘
       │          │          │          │
       ▼          ▼          ▼          ▼
┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────────────┐
│ Content  │ │ Document │ │ Database │ │ DocumentType         │
│ Under-   │ │ Field    │ │ Service  │ │ Configuration        │
│ standing │ │ Extractor│ │          │ │                      │
│ Service  │ │          │ │          │ │ Category → DocType   │
│          │ │          │ │          │ │ DocType → AnalyzerId │
│ •Classify│ │ •Process │ │ •Upsert  │ └──────────────────────┘
│ •Extract │ │  fields  │ │  Feature │
│ •Score   │ │ •Conf.   │ │  Data    │
│  category│ │  gating  │ │ •Insert  │
│ •Schema  │ │ •Conf.   │ │  Data    │
│  methods │ │ •Citation│ │  Job     │
│ •Ensure  │ │ •Status  │ │  Details │
│  analyzer│ │          │ │  Details │
│  exists  │ ├──────────┤ └──────────┘
└──────────┘ │ Citation │
             │ Service  │
             │          │
             │ •Parse   │
             │  extract │
             │  source  │
             │ •Compute │
             │  generate│
             │  confid. │
             ├──────────┤
             │FeatureRef│
             │ Service  │
             │          │
             │ •Lookup  │
             │  FeatureId│
             │ •Auto-   │
             │  create  │
             └──────────┘
```

---

## Data Flow

```
 PDF Upload                Azure Content Understanding              SQL Server
 ─────────                 ──────────────────────────               ──────────
                                                                   
 genpact/                                                          
 ├── doc.pdf ────┐                                                 
                 │         ┌─────────────────────┐                 
                 ├────────►│  Classifier API      │                 
                 │         │  (doc_classifier_    │                 
                 │         │   ..._score_other)   │                 
                 │         └─────────┬───────────┘                 
                 │                   │                              
                 │          Segments: [{CRE, p1-50},               
                 │                    {Valuation, p51-200}]        
                 │                   │                              
                 │         ┌─────────▼───────────┐                 
                 ├────────►│  GPT Confidence      │                 
                 │         │  Scoring (20%        │                 
                 │         │  excerpt sample)     │                 
                 │         └─────────┬───────────┘                 
                 │                   │                              
                 │      score < 70 or category=Other?              
                 │                   │                              
                 │             yes ──┴──► stop / human review      
                 │                   │                              
                 │                  no                              
                 │                   │                              
                 │         ┌─────────▼───────────┐    ┌──────────┐
                 ├────────►│  Extraction API      │    │FeatureRef│
                 │         │  (per-segment        │    │  Table   │
                 │         │   analyzer)          │    │          │
                 │         └─────────┬───────────┘    └────┬─────┘
                 │                   │                      │      
                 │          Fields + Citations + Markdown   │      
                 │                   │                      │      
                 │         ┌─────────▼───────────┐         │      
                 │         │ Field Processing     │◄────────┘      
                 │         │  • extract vs generate│                
                 │         │  • confidence scoring │                
                 │         │  • citation parsing   │                
                 │         └─────────┬───────────┘                 
                                     │                              
                            ┌────────▼────────┐                    
                            │  dbo.FeatureData │                    
                            │  dbo.JobDetails  │                    
                            └─────────────────┘                    
```

---

## Analyzer Schema Mapping

| Classifier Category | DocumentType | Analyzer ID                  | Fields | Generate Fields |
|---------------------|-------------|------------------------------|--------|-----------------|
| **CRE**             | Loan        | `cre_loan_analyzer`          | 9      | 0               |
| **Valuation**       | Appraisal   | `appraisal_report_analyzer`  | 22     | 4 *             |
| **CNI**             | CNI         | `cni_agreement_analyzer`     | 9      | 0               |
| **Other**           | Other       | None                         | 0      | 0               |

\* Generate fields for Appraisal: `Sales_Comparison_Value`, `Direct_Capitalization_Value`, `DCF_Analysis_Value`, `Cost_Approach_Value`

---

## Database Tables

| Table | Purpose |
|-------|---------|
| `dbo.FeatureRef` | Master list of field names → FeatureId mapping |
| `dbo.FeatureData` | Extracted field values with confidence, citations, and method |
| `dbo.JobDetails` | Job tracking for documents that proceed past the confidence gate |
| `dbo.SME` | SME master list: name, doc type specialty, max concurrent docs, active status |
| `dbo.DocumentAssignment` | Tracks which document is assigned to which SME for HITL review |
| `dbo.RoundRobinPointer` | Remembers the last assigned SME per doc type for round-robin rotation |
---

## Key Configuration (local.settings.json)

| Setting | Description |
|---------|-------------|
| `AzureWebJobsStorage` | Blob storage connection string (trigger source) |
| `AZURE_CONTENT_UNDERSTANDING_ENDPOINT` | Azure AI Content Understanding endpoint |
| `AZURE_CONTENT_UNDERSTANDING_API_KEY` | API key for Content Understanding |
| `ConnectionStrings:SqlConnectionString` | SQL Server connection string |

---

## Project Structure

```
DocClassifyExtract/
├── ClassifyAndExtractFunction.cs    # Orchestrator (blob trigger entry point)
├── Program.cs                       # DI registration & host setup
├── host.json                        # Azure Functions host config
├── local.settings.json              # Environment variables & connection strings
│
├── Configuration/
│   └── DocumentTypeConfiguration.cs # Category → DocType → Analyzer mapping
│
├── Models/
│   ├── DocumentModels.cs            # ExtractedFieldResult, FieldCitation, JobDetails, enums
│   └── SmeModels.cs                 # Sme, DocumentAssignment models
│
├── Services/
│   ├── ContentUnderstandingService.cs  # Azure AI API calls (classify, confidence score, extract, schema)
│   ├── DocumentFieldExtractor.cs       # Field processing, confidence, method detection
│   ├── CitationService.cs              # Citation parsing & generate-field text matching
│   ├── FeatureRefService.cs            # FeatureId lookup/auto-creation from SQL
│   ├── DatabaseService.cs             # SQL upsert for FeatureData & JobDetails
│   └── SmeAssignmentService.cs        # Round-robin SME assignment for HITL review
│
├── sql/
│   └── create_sme_tables.sql          # SQL script to create SME/assignment tables
│
└── analyzer-schemas/
    ├── appraisal_report_analyzer.json  # 22 fields (18 extract, 4 generate)
    ├── cni_agreement_analyzer.json     # 9 fields (all extract)
    └── cre_loan_analyzer.json          # 9 fields (all extract)
```

---

## SME Round-Robin Assignment (HITL Review)

After field extraction, if any extracted field has `ReviewRequired = true`, the document can be assigned to an SME for human review.

Documents classified as `Other` or with classification confidence score below `70%` do not reach DB save or SME assignment. They stop early and require manual intervention outside the normal extraction pipeline.

### How It Works

```
Fields saved to DB → Any ReviewRequired? → Yes → Read RoundRobinPointer for DocType
                                                         ↓
                                           Pick next SME in sequence
                                                         ↓
                                           Under max capacity? → Assign + update pointer
                                                         ↓ (if at capacity)
                                           Skip, try next SME → All full? → Unassigned
```

### Tables

- **dbo.SME** — Master list of SMEs with `DocType` (CRE/C&I/Valuation), `MaxConcurrentDocs`, `IsActive`
- **dbo.DocumentAssignment** — Assignment record: DocumentId → SmeId, Status (Assigned/Completed)
- **dbo.RoundRobinPointer** — One row per DocType, stores `LastAssignedSmeId`

### Assignment Logic

1. Get all active SMEs for the document's type, ordered by `SmeId`
2. Read the pointer to find who was assigned last
3. Start from the next SME in the list (wraps around)
4. Skip any SME whose current `Assigned` count ≥ `MaxConcurrentDocs`
5. Assign and update pointer
6. If all are at capacity, document stays unassigned (can be retried later)
