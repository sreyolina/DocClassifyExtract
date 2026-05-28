from docx import Document
from docx.shared import Pt, Inches, Cm, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml.ns import qn

doc = Document()

# ─── Styles ───────────────────────────────────────────────────────────────────
style = doc.styles['Normal']
font = style.font
font.name = 'Calibri'
font.size = Pt(11)

# ─── Title Page ───────────────────────────────────────────────────────────────
doc.add_paragraph()
doc.add_paragraph()
title = doc.add_heading('Document Classification & Extraction Solution', level=0)
title.alignment = WD_ALIGN_PARAGRAPH.CENTER

subtitle = doc.add_paragraph()
subtitle.alignment = WD_ALIGN_PARAGRAPH.CENTER
run = subtitle.add_run('Intelligent Document Processing Pipeline')
run.font.size = Pt(16)
run.font.color.rgb = RGBColor(0x44, 0x72, 0xC4)

doc.add_paragraph()
meta = doc.add_paragraph()
meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
meta.add_run('Genpact | Azure AI Solution').font.size = Pt(14)

doc.add_paragraph()
doc.add_paragraph()
info = doc.add_paragraph()
info.alignment = WD_ALIGN_PARAGRAPH.CENTER
info.add_run('Date: May 2026\n').font.size = Pt(12)
info.add_run('Platform: Azure Functions v4 (.NET 8)\n').font.size = Pt(12)
info.add_run('AI Services: Azure Content Understanding + GPT-4.1').font.size = Pt(12)

doc.add_page_break()

# ─── Table of Contents ────────────────────────────────────────────────────────
doc.add_heading('Table of Contents', level=1)
toc_items = [
    '1. Solution Overview',
    '2. Architecture & Services',
    '3. Document Classification Pipeline',
    '4. GPT-4.1 Confidence Scoring',
    '5. Field Extraction',
    '6. Database Persistence',
    '7. SME Assignment (Human-in-the-Loop)',
    '8. Blob Routing & File Organization',
    '9. Document Categories & Indicators',
    '10. End-to-End Flow Diagram',
    '11. Technology Stack',
]
for item in toc_items:
    doc.add_paragraph(item, style='List Number')

doc.add_page_break()

# ─── 1. Solution Overview ────────────────────────────────────────────────────
doc.add_heading('1. Solution Overview', level=1)

doc.add_paragraph(
    'This solution provides an end-to-end intelligent document processing pipeline that automatically '
    'classifies incoming documents, validates the classification using AI, extracts structured data, '
    'and routes documents to the appropriate teams for review.'
)

doc.add_paragraph()
doc.add_heading('Key Capabilities', level=2)
capabilities = [
    'Automatic document classification into CRE (Commercial Real Estate), CNI (Commercial & Industrial), Valuation, or Other',
    'AI-powered confidence scoring using GPT-4.1 to validate classification accuracy',
    'Structured field extraction using document-type-specific analyzers',
    'Citation mapping with page numbers, bounding boxes, and highlight text',
    'Human-in-the-Loop (HITL) flagging for low-confidence or ambiguous documents',
    'Round-robin SME assignment for documents requiring review',
    'Automated blob routing to classified folders',
    'Full audit trail with job status tracking in SQL Server',
]
for cap in capabilities:
    doc.add_paragraph(cap, style='List Bullet')

doc.add_page_break()

# ─── 2. Architecture & Services ──────────────────────────────────────────────
doc.add_heading('2. Architecture & Services', level=1)

doc.add_paragraph(
    'The solution is built as an Azure Function triggered by blob uploads. It orchestrates '
    'multiple services in a defined sequence:'
)

# Services table
table = doc.add_table(rows=8, cols=3)
table.style = 'Medium Shading 1 Accent 1'
table.alignment = WD_TABLE_ALIGNMENT.CENTER

headers = ['Service', 'Technology', 'Responsibility']
for i, header in enumerate(headers):
    table.rows[0].cells[i].text = header

services_data = [
    ['ContentUnderstandingService', 'Azure AI Content Understanding API', 'Document classification + field extraction via analyzers'],
    ['GPT-4.1 Confidence Scorer', 'Azure OpenAI (GPT-4.1)', 'Validates classification with reasoning and indicator matching'],
    ['DocumentFieldExtractor', 'Custom .NET Service', 'Parses raw API responses into structured fields with citations'],
    ['DatabaseService', 'Azure SQL Server', 'Persists extracted fields, job details, and audit trail'],
    ['SmeAssignmentService', 'Azure SQL Server', 'Assigns documents to SMEs via round-robin for HITL review'],
    ['BlobRoutingService', 'Azure Blob Storage', 'Moves documents to classified folders after processing'],
    ['CitationService', 'Custom .NET Service', 'Maps field values to specific page locations and bounding boxes'],
]
for row_idx, row_data in enumerate(services_data, start=1):
    for col_idx, cell_data in enumerate(row_data):
        table.rows[row_idx].cells[col_idx].text = cell_data

doc.add_paragraph()
doc.add_heading('Trigger Mechanism', level=2)
doc.add_paragraph(
    'The pipeline is triggered automatically when a document is uploaded to the Azure Blob Storage container:'
)
p = doc.add_paragraph()
p.add_run('Container: ').bold = True
p.add_run('genpact')
p = doc.add_paragraph()
p.add_run('Path: ').bold = True
p.add_run('incoming-documents/{filename}')
p = doc.add_paragraph()
p.add_run('Supported Formats: ').bold = True
p.add_run('PDF, DOCX, and other document formats')

doc.add_page_break()

# ─── 3. Document Classification Pipeline ─────────────────────────────────────
doc.add_heading('3. Document Classification Pipeline', level=1)

doc.add_heading('Step 1: Classifier Provisioning', level=2)
doc.add_paragraph(
    'The system automatically provisions an Azure Content Understanding classifier with the ID:'
)
p = doc.add_paragraph()
p.add_run('doc_classifier_cre_cni_valuation_confidence_score_other').bold = True

doc.add_paragraph(
    'This classifier is configured with detailed category descriptions and strong indicators '
    'for each document type. It uses a pre-built document model as the base analyzer and GPT-4.1 '
    'as the completion model for intelligent classification.'
)

doc.add_heading('Step 2: Document Submission', level=2)
doc.add_paragraph('The document is submitted to the Azure Content Understanding API:')
p = doc.add_paragraph()
p.add_run('API Endpoint: ').bold = True
p.add_run('{endpoint}/contentunderstanding/analyzers/{classifierId}:analyze')
p = doc.add_paragraph()
p.add_run('API Version: ').bold = True
p.add_run('2025-11-01')
p = doc.add_paragraph()
p.add_run('Authentication: ').bold = True
p.add_run('Ocp-Apim-Subscription-Key header')

doc.add_paragraph(
    'The API processes the document asynchronously. The system polls the Operation-Location '
    'URL until classification completes (with exponential backoff, max 900 seconds).'
)

doc.add_heading('Step 3: Classification Results', level=2)
doc.add_paragraph('The classifier returns segments with:')
results = [
    'Category — The document type (CRE, CNI, Valuation, or Other)',
    'Confidence — Native classifier confidence score (0.0 to 1.0)',
    'Start/End Page Numbers — Page range for each classified segment',
    'Markdown Content — Full text extraction for downstream processing',
]
for r in results:
    doc.add_paragraph(r, style='List Bullet')

doc.add_page_break()

# ─── 4. GPT-4.1 Confidence Scoring ──────────────────────────────────────────
doc.add_heading('4. GPT-4.1 Confidence Scoring', level=1)

doc.add_paragraph(
    'After the initial classification, the system performs a second-level validation using GPT-4.1. '
    'This acts as a quality gate to ensure classification accuracy before proceeding to extraction.'
)

doc.add_heading('How It Works', level=2)
steps = [
    'Extract a representative page sample (20% of document pages) as markdown text',
    'Send the excerpt + assigned category to GPT-4.1 with a structured scoring prompt',
    'GPT-4.1 evaluates whether the document matches the assigned category based on domain-specific indicators',
    'Returns a confidence percentage (0-99) with detailed reasoning',
]
for i, step in enumerate(steps, 1):
    doc.add_paragraph(f'{i}. {step}')

doc.add_heading('Scoring Rules', level=2)
scoring_table = doc.add_table(rows=6, cols=2)
scoring_table.style = 'Medium Shading 1 Accent 1'
scoring_table.rows[0].cells[0].text = 'Score Range'
scoring_table.rows[0].cells[1].text = 'Meaning'
scoring_data = [
    ['91-99%', 'Document overwhelmingly matches — many strong indicators present, no competing signals'],
    ['70-89%', 'Document clearly matches — several strong indicators but not all present'],
    ['50-69%', 'Ambiguous — some indicators match but competing signals exist'],
    ['30-49%', 'Weak match — another category may be more appropriate'],
    ['0-29%', 'Likely misclassified'],
]
for row_idx, (score, meaning) in enumerate(scoring_data, start=1):
    scoring_table.rows[row_idx].cells[0].text = score
    scoring_table.rows[row_idx].cells[1].text = meaning

doc.add_paragraph()
doc.add_heading('Confidence Threshold', level=2)
p = doc.add_paragraph()
p.add_run('Threshold: 70%').bold = True
doc.add_paragraph(
    'If the GPT confidence score is below 70%, the document is flagged for human intervention '
    'and extraction is skipped. This ensures that only confidently classified documents proceed '
    'through the automated extraction pipeline.'
)

doc.add_heading('GPT Response Format', level=2)
doc.add_paragraph('GPT-4.1 returns structured JSON with:')
gpt_fields = [
    'confidence_percent — Integer score (0-99)',
    'reasoning — 2-3 sentence explanation of the score',
    'matched_indicators — List of category indicators found in the document',
    'missing_indicators — List of expected indicators not found',
    'competing_category — If another category might be a better fit',
    'competing_confidence_percent — Confidence for the competing category',
]
for f in gpt_fields:
    doc.add_paragraph(f, style='List Bullet')

doc.add_page_break()

# ─── 5. Field Extraction ─────────────────────────────────────────────────────
doc.add_heading('5. Field Extraction', level=1)

doc.add_paragraph(
    'After classification passes the confidence threshold, the system extracts structured fields '
    'using document-type-specific analyzers.'
)

doc.add_heading('Extraction Analyzers', level=2)
analyzer_table = doc.add_table(rows=4, cols=3)
analyzer_table.style = 'Medium Shading 1 Accent 1'
analyzer_table.rows[0].cells[0].text = 'Document Type'
analyzer_table.rows[0].cells[1].text = 'Analyzer ID'
analyzer_table.rows[0].cells[2].text = 'Schema File'
analyzer_data = [
    ['Valuation (Appraisal)', 'appraisal_report_analyzer', 'appraisal_report_analyzer.json'],
    ['CRE (Loan)', 'cre_loan_analyzer', 'cre_loan_analyzer.json'],
    ['CNI (Commercial & Industrial)', 'cni_agreement_analyzer', 'cni_agreement_analyzer.json'],
]
for row_idx, row_data in enumerate(analyzer_data, start=1):
    for col_idx, cell_data in enumerate(row_data):
        analyzer_table.rows[row_idx].cells[col_idx].text = cell_data

doc.add_paragraph()
doc.add_heading('Field Methods', level=2)
doc.add_paragraph('Each field in an analyzer schema has a method:')
doc.add_paragraph('Extract — Value is pulled directly from the document text (OCR/layout-based)', style='List Bullet')
doc.add_paragraph('Generate — Value is inferred/summarized by the AI model from context', style='List Bullet')

doc.add_heading('Field-Level Confidence', level=2)
doc.add_paragraph(
    'Each extracted field includes a confidence score. Fields with confidence below 0.5 are flagged '
    'as requiring human review (HITL). Their value is set to "H-I-T-L" to indicate that a subject '
    'matter expert needs to verify/correct the value.'
)

doc.add_heading('Citations', level=2)
doc.add_paragraph('Each extracted field is enriched with citation data:')
citation_fields = [
    'Page number where the value was found',
    'PDF link with SAS token for direct access',
    'Span offset and length in the document text',
    'Highlight text showing the exact source passage',
    'Bounding box coordinates (X, Y, Width, Height) for visual highlighting',
]
for c in citation_fields:
    doc.add_paragraph(c, style='List Bullet')

doc.add_page_break()

# ─── 6. Database Persistence ─────────────────────────────────────────────────
doc.add_heading('6. Database Persistence', level=1)

doc.add_paragraph(
    'All extracted data is persisted to Azure SQL Server using MERGE (upsert) operations.'
)

doc.add_heading('Tables', level=2)

doc.add_heading('FeatureData Table', level=3)
doc.add_paragraph('Stores each extracted field with full metadata:')
feature_fields = [
    'DocumentId — Unique document identifier (derived from filename)',
    'FeatureId — Numeric ID mapped from field name',
    'FieldName — Human-readable field name',
    'Value — Extracted or generated value',
    'Confidence — Field-level confidence (0.0 to 1.0)',
    'FieldMethod — "extract" or "generate"',
    'ConfidenceReason — Explanation of the confidence score',
    'ReviewRequired — Boolean flag for HITL review',
    'Citation fields — PageNumber, PdfLink, SpanOffset, SpanLength, HighlightText, BoundingBox',
]
for f in feature_fields:
    doc.add_paragraph(f, style='List Bullet')

doc.add_heading('JobDetails Table', level=3)
doc.add_paragraph('Tracks the processing status of each document:')
job_fields = [
    'JobId — Unique processing job identifier',
    'DocumentId — Links to the source document',
    'Status — "Successful", "Partially Successful", "Failed", or "No Fields Extracted"',
]
for f in job_fields:
    doc.add_paragraph(f, style='List Bullet')

doc.add_page_break()

# ─── 7. SME Assignment ───────────────────────────────────────────────────────
doc.add_heading('7. SME Assignment (Human-in-the-Loop)', level=1)

doc.add_paragraph(
    'When extracted fields require human review (confidence < 0.5), the document is automatically '
    'assigned to a Subject Matter Expert (SME) for verification.'
)

doc.add_heading('Round-Robin Assignment', level=2)
doc.add_paragraph('The assignment algorithm:')
assignment_steps = [
    'Identifies active SMEs for the document type (CRE, CNI, Valuation)',
    'Retrieves the last assigned SME pointer from the RoundRobinPointer table',
    'Selects the next eligible SME (must be active and under concurrent limit)',
    'Creates an assignment record and updates the pointer',
]
for i, step in enumerate(assignment_steps, 1):
    doc.add_paragraph(f'{i}. {step}')

doc.add_heading('Reassignment', level=2)
doc.add_paragraph(
    'Documents can be reassigned to different SMEs if needed. The system marks the current '
    'assignment as "Reassigned" and creates a new assignment for the target SME.'
)

doc.add_heading('When Human Intervention is Required', level=2)
intervention_cases = [
    'Document classified as "Other" (unknown category)',
    'GPT confidence score below 70% for the assigned category',
    'Individual field confidence below 0.5 (field-level HITL)',
]
for case in intervention_cases:
    doc.add_paragraph(case, style='List Bullet')

doc.add_page_break()

# ─── 8. Blob Routing ─────────────────────────────────────────────────────────
doc.add_heading('8. Blob Routing & File Organization', level=1)

doc.add_paragraph(
    'After successful processing, documents are automatically moved from the intake folder '
    'to their classified destination folder.'
)

doc.add_heading('Routing Rules', level=2)
routing_table = doc.add_table(rows=4, cols=2)
routing_table.style = 'Medium Shading 1 Accent 1'
routing_table.rows[0].cells[0].text = 'Document Type'
routing_table.rows[0].cells[1].text = 'Destination Folder'
routing_data = [
    ['CRE (Loan)', 'genpact/cre_loan/'],
    ['Valuation (Appraisal)', 'genpact/cre_valuation/'],
    ['CNI', 'genpact/cni/'],
]
for row_idx, (doc_type, dest) in enumerate(routing_data, start=1):
    routing_table.rows[row_idx].cells[0].text = doc_type
    routing_table.rows[row_idx].cells[1].text = dest

doc.add_paragraph()
doc.add_heading('File Naming Convention', level=2)
doc.add_paragraph(
    'Routed documents are renamed based on extracted metadata (borrower name, document type, date) '
    'to enable easy identification and search.'
)

doc.add_heading('Routing is Skipped When', level=2)
skip_cases = [
    'Document category is "Other"',
    'Confidence score is below 70% (requires human intervention)',
    'Processing encountered errors',
]
for case in skip_cases:
    doc.add_paragraph(case, style='List Bullet')

doc.add_page_break()

# ─── 9. Document Categories & Indicators ─────────────────────────────────────
doc.add_heading('9. Document Categories & Indicators', level=1)

doc.add_heading('Valuation (Appraisal Reports)', level=2)
doc.add_paragraph(
    'Appraisal or valuation reports focused on estimating property value and collateral value '
    'using valuation methodology and appraisal standards.'
)
doc.add_paragraph()
p = doc.add_paragraph()
p.add_run('Strong Indicators:').bold = True
valuation_indicators = [
    'Effective age, remaining economic life',
    'USPAP compliance, RICS Red Book valuation',
    'Comparable sales analysis, paired sales analysis',
    'Yield capitalization, band of investment',
    'Scope of work, neighborhood analysis',
    'Site coverage ratio, floor area ratio',
    'Going concern value, business enterprise value',
    'Retrospective/prospective valuation',
]
for ind in valuation_indicators:
    doc.add_paragraph(ind, style='List Bullet')

doc.add_heading('CRE (Commercial Real Estate Loans)', level=2)
doc.add_paragraph(
    'Commercial real estate loan agreements or legal credit packages secured by real property, '
    'with covenant, collateral control, tenant controls, and cash-flow control language.'
)
doc.add_paragraph()
p = doc.add_paragraph()
p.add_run('Strong Indicators:').bold = True
cre_indicators = [
    'Assignment of Leases and Rents',
    'Tenant estoppel certificates',
    'Non-recourse carveouts / bad boy guaranty',
    'DSCR triggers, stabilization conditions',
    'Property cash flow waterfall, net cash flow sweep',
    'Mortgage deed / deed of trust',
    'Environmental indemnity agreement',
    'Occupancy covenants, minimum occupancy requirement',
]
for ind in cre_indicators:
    doc.add_paragraph(ind, style='List Bullet')

doc.add_heading('CNI (Commercial & Industrial)', level=2)
doc.add_paragraph(
    'Commercial and industrial loan agreements, corporate credit agreements, borrowing base documents, '
    'and general business lending packages not primarily CRE or valuation.'
)
doc.add_paragraph()
p = doc.add_paragraph()
p.add_run('Strong Indicators:').bold = True
cni_indicators = [
    'Revolving and term loan mechanics',
    'Corporate financial covenants',
    'Borrower and guarantor corporate credit terms',
    'Collateral structures not centered on real estate',
    'Borrowing base calculations',
]
for ind in cni_indicators:
    doc.add_paragraph(ind, style='List Bullet')

doc.add_heading('Other', level=2)
doc.add_paragraph(
    'Documents that do not clearly fall into CRE, CNI, or Valuation. These are automatically '
    'flagged for human intervention with no extraction performed.'
)

doc.add_page_break()

# ─── 10. End-to-End Flow Diagram ─────────────────────────────────────────────
doc.add_heading('10. End-to-End Flow Diagram', level=1)

doc.add_paragraph(
    'The following describes the complete processing pipeline from document upload to final storage:'
)

flow_steps = [
    ('Step 1: Upload', 'Document is uploaded to Azure Blob Storage (genpact/incoming-documents/)'),
    ('Step 2: Trigger', 'Azure Function is automatically triggered by the blob upload event'),
    ('Step 3: Classify', 'Document is submitted to Azure Content Understanding classifier API'),
    ('Step 4: Parse Results', 'Classification returns category (CRE/CNI/Valuation/Other) + native confidence per segment'),
    ('Step 5: GPT Validation', 'GPT-4.1 validates each segment\'s classification by analyzing document indicators'),
    ('Step 6: Confidence Gate', 'If GPT score ≥ 70%: proceed | If < 70% or "Other": flag for human intervention'),
    ('Step 7: Extract Fields', 'Document is sent to the appropriate extraction analyzer (per document type)'),
    ('Step 8: Parse & Enrich', 'Raw extraction results are parsed into structured fields with citations and confidence'),
    ('Step 9: Save to DB', 'All fields and job metadata are persisted to Azure SQL Server'),
    ('Step 10: SME Assignment', 'If any field needs HITL review, document is assigned to an SME via round-robin'),
    ('Step 11: Route Blob', 'Document is moved from incoming-documents/ to the classified folder'),
    ('Step 12: Complete', 'Processing complete — full audit trail available in database'),
]

for step_title, step_desc in flow_steps:
    p = doc.add_paragraph()
    p.add_run(f'{step_title}: ').bold = True
    p.add_run(step_desc)

doc.add_paragraph()
doc.add_heading('Decision Points', level=2)

decision_table = doc.add_table(rows=4, cols=3)
decision_table.style = 'Medium Shading 1 Accent 1'
decision_table.rows[0].cells[0].text = 'Decision'
decision_table.rows[0].cells[1].text = 'Condition'
decision_table.rows[0].cells[2].text = 'Action'
decisions = [
    ['Category = "Other"', 'Classifier assigns unknown category', 'Skip extraction → Human intervention'],
    ['GPT Score < 70%', 'Low confidence in classification', 'Skip extraction → Human intervention'],
    ['Field Confidence < 0.5', 'Extracted value uncertain', 'Flag field for HITL review, assign to SME'],
]
for row_idx, row_data in enumerate(decisions, start=1):
    for col_idx, cell_data in enumerate(row_data):
        decision_table.rows[row_idx].cells[col_idx].text = cell_data

doc.add_page_break()

# ─── 11. Technology Stack ─────────────────────────────────────────────────────
doc.add_heading('11. Technology Stack', level=1)

tech_table = doc.add_table(rows=9, cols=2)
tech_table.style = 'Medium Shading 1 Accent 1'
tech_table.rows[0].cells[0].text = 'Component'
tech_table.rows[0].cells[1].text = 'Technology'
tech_data = [
    ['Runtime', 'Azure Functions v4 (.NET 8 Isolated Worker)'],
    ['Classification', 'Azure AI Content Understanding (API version 2025-11-01)'],
    ['Confidence Scoring', 'Azure OpenAI GPT-4.1 (API version 2024-12-01-preview)'],
    ['Storage', 'Azure Blob Storage'],
    ['Database', 'Azure SQL Server'],
    ['Authentication', 'API Key (Ocp-Apim-Subscription-Key)'],
    ['Trigger', 'Blob Trigger (automatic on upload)'],
    ['Language', 'C# 12 / .NET 8'],
]
for row_idx, (component, tech) in enumerate(tech_data, start=1):
    tech_table.rows[row_idx].cells[0].text = component
    tech_table.rows[row_idx].cells[1].text = tech

doc.add_paragraph()
doc.add_heading('Azure Resources', level=2)
resources = [
    'Azure AI Services (Content Understanding + OpenAI) — doc-classify-foundry.cognitiveservices.azure.com',
    'Azure Storage Account — storagegenpactmvp2',
    'Azure SQL Database — genpactpoc on docstream.database.windows.net',
    'Azure Functions App — DocClassifyExtract',
]
for r in resources:
    doc.add_paragraph(r, style='List Bullet')

# ─── Save ─────────────────────────────────────────────────────────────────────
output_path = r'c:\Users\somolinasaha\Downloads\Genpact\DocClassifyExtract\DocClassifyExtract\DocClassifyExtract_Solution_Overview.docx'
doc.save(output_path)
print(f'Document saved to: {output_path}')
