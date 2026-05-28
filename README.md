# DocClassifyExtract — Azure Functions Document Processing Pipeline

## Overview

An Azure Functions v4 (.NET 8 isolated worker) application that classifies uploaded documents, validates the classification with GPT-based confidence scoring, extracts structured fields for high-confidence known document types, stores successful extraction results in SQL Server, assigns documents to SMEs for HITL review via round-robin, and routes classified blobs to organized destination folders.

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
│  │   • Classifier API Version: 2025-11-01                              │    │
│  │   • Extraction API Version: 2025-05-01-preview                      │    │
│  │   • GPT Model: gpt-4.1 (API: 2024-12-01-preview)                   │    │
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
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 7: SME Assignment (HITL Review)                                │    │
│  │   • Only if any extracted field has ReviewRequired = true           │    │
│  │   • Round-robin assignment using dbo.RoundRobinPointer              │    │
│  │   • Checks SME capacity (MaxConcurrentDocs) before assigning        │    │
│  │   • Inserts into dbo.DocumentAssignment                             │    │
│  │   • If all SMEs at capacity → document stays unassigned             │    │
│  └────────────────────────────┬────────────────────────────────────────┘    │
│                               │                                             │
│                               ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ STEP 8: Blob Routing                                                │    │
│  │   • Skipped if category = Other or confidence < 70%                 │    │
│  │   • Copies blob to classified destination folder:                   │    │
│  │     ─ Loan      → genpact/cre_loan/                                │    │
│  │     ─ Appraisal → genpact/cre_valuation/                           │    │
│  │     ─ CNI       → genpact/cni/                                     │    │
│  │   • Renames using extracted fields:                                 │    │
│  │     ─ CRE: CRE_{RelationshipName}_CreditAgreement_{LoanDate}       │    │
│  │     ─ Valuation: CRE_{ClientName}_ValuationReport_{ValDate}         │    │
│  │     ─ CNI: C&I_{BorrowerName}_CreditAgreement_{AgreementDate}       │    │
│  │   • Deletes the original from incoming-documents/                   │    │
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
│  ├── IBlobRoutingService           (classified blob routing)  │
│  ├── ISmeAssignmentService         (round-robin SME assign)   │
│  └── BlobServiceClient             (SAS URL generation)       │
└──────┬──────────┬──────────┬──────────┬──────────┬──────────┘
       │          │          │          │          │
       ▼          ▼          ▼          ▼          ▼
┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌─────────────────────┐
│ Content  │ │ Document │ │ Database │ │ Blob     │ │ Sme Assignment      │
│ Under-   │ │ Field    │ │ Service  │ │ Routing  │ │ Service             │
│ standing │ │ Extractor│ │          │ │ Service  │ │                     │
│ Service  │ │          │ │          │ │          │ │ •Round-robin        │
│          │ │          │ │          │ │ •Copy to │ │  assignment         │
│ •Classify│ │ •Process │ │ •Upsert  │ │  dest    │ │ •Capacity           │
│ •Extract │ │  fields  │ │  Feature │ │  folder  │ │  checking           │
│ •Score   │ │ •Conf.   │ │  Data    │ │ •Rename  │ │ •Pointer            │
│  category│ │  gating  │ │ •Insert  │ │  with    │ │  update             │
│ •Schema  │ │ •Conf.   │ │  Job     │ │  fields  │ └─────────────────────┘
│  methods │ │ •Citation│ │  Details │ │ •Delete  │
│ •Ensure  │ │ •Status  │ │          │ │  source  │
│  analyzer│ │          │ └──────────┘ └──────────┘
│  exists  │ ├──────────┤
└──────────┘ │ Citation │ ┌──────────────────────┐
             │ Service  │ │ DocumentType         │
             │          │ │ Configuration        │
             │ •Parse   │ │                      │
             │  extract │ │ Category → DocType   │
             │  source  │ │ DocType → AnalyzerId │
             │ •Compute │ └──────────────────────┘
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
                            └────────┬────────┘                    
                                     │                              
                       ┌─────────────┼─────────────┐               
                       │                           │               
                       ▼                           ▼               
              ┌─────────────────┐    ┌───────────────────────┐     
              │ SME Assignment  │    │ Blob Routing           │     
              │ (round-robin)   │    │ incoming-documents/ →  │     
              │                 │    │   cre_loan/            │     
              │ dbo.Document-   │    │   cre_valuation/       │     
              │   Assignment    │    │   cni/                 │     
              │ dbo.RoundRobin- │    │                        │     
              │   Pointer       │    │ Rename with fields +   │     
              └─────────────────┘    │ delete original        │     
                                     └───────────────────────┘                    
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
│   ├── BlobRoutingService.cs          # Routes classified blobs to destination folders
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
- **dbo.DocumentAssignment** — Assignment record: DocumentId → SmeId, Status (Assigned/Completed/Reassigned)
- **dbo.RoundRobinPointer** — One row per DocType, stores `LastAssignedSmeId`

### Assignment Logic

1. Get all active SMEs for the document's type, ordered by `SmeId`
2. Read the pointer to find who was assigned last
3. Start from the next SME in the list (wraps around)
4. Skip any SME whose current `Assigned` count ≥ `MaxConcurrentDocs`
5. Assign and update pointer
6. If all are at capacity, document stays unassigned (can be retried later)

### Reassignment

Documents can be reassigned from one SME to another via `ReassignDocumentAsync`. This supports the UI scenario where a reviewer selects a different SME from a dropdown.

**Rules:**
- Reassignment is for the **entire document**, not per-field
- The reassignment dropdown shows **all active SMEs** for that document type (`GetAvailableSmesForDocTypeAsync`)
- No seniority is considered — any eligible SME can be selected
- The target SME must be active, handle the same DocType, and not be at capacity

**Flow:**
1. Find the current active assignment for the document
2. Validate the target SME (active, correct DocType, under capacity)
3. Mark the existing assignment as `'Reassigned'` with a completion timestamp
4. Create a new assignment record for the target SME
5. All steps run in a single SQL transaction (atomic)

---

## Blob Routing (Post-Processing)

After successful extraction and DB save, the original blob is moved from the trigger folder to a classified destination folder with a descriptive filename built from extracted fields.

### Routing Rules

| DocumentType | Destination Folder      | Filename Pattern                                        |
|-------------|-------------------------|---------------------------------------------------------|
| Loan        | `genpact/cre_loan/`      | `CRE_{Relationship_Name}_CreditAgreement_{Original_Loan_Date}.pdf` |
| Appraisal   | `genpact/cre_valuation/` | `CRE_{Client_Name}_ValuationReport_{Date_Of_Valuation}.pdf`        |
| CNI         | `genpact/cni/`           | `C&I_{Borrower_Name}_CreditAgreement_{Agreement_Date}.pdf`         |

### Behavior

- Skipped entirely if the document category is `Other` or confidence score < 70%
- Date fields are formatted as `MMddyyyy`; missing dates become `UnknownDate`
- Missing name fields become `Unknown`
- Invalid filename characters are replaced with hyphens
- After successful copy, the original blob in `incoming-documents/` is deleted
- Server-side copy is used (no data downloaded to the function)
