# Document Classification & Extraction - Test Results

**Test Date:** May 26-27, 2026  
**Environment:** Azure Functions v4 (.NET 8 Isolated Worker)  
**Classifier:** `doc_classifier_cre_cni_valuation_confidence_score_other`  
**Confidence Scoring:** GPT-4.1  
**Confidence Threshold:** 70%  

---

## System Architecture

| Component | Details |
|-----------|---------|
| Trigger | Blob trigger on `genpact/incoming-documents/{name}` |
| Classifier | Azure Content Understanding (4 categories: CNI, CRE, Valuation, Other) |
| Confidence Scoring | GPT-4.1 secondary validation (0-99 scale) |
| Storage | Azure Blob Storage (`storagegenpactmvp2`) |
| Database | Azure SQL (`docstream.database.windows.net` / `genpactpoc`) |
| Routing | Automatic blob routing to category-specific folders |

---

## Classification Workflow

```
Document Upload → Azure Classifier → Category Assignment
                                          ↓
                              ┌─── "Other" → 0% confidence → Human Intervention
                              │                              (Skip DB, SME, Routing)
                              │
                              └─── CNI/CRE/Valuation → GPT-4.1 Confidence Scoring
                                                              ↓
                                              ┌─── ≥ 70% → Full Processing
                                              │            (Extract, DB Save, SME Assign, Route)
                                              │
                                              └─── < 70% → Human Intervention
                                                           (Extract, Skip DB, Skip SME, Skip Routing)
```

---

## Test Results Summary

| Category | Tests | Passed | Failed |
|----------|:-----:|:------:|:------:|
| True Positive (CNI) | 2 | 2 | 0 |
| True Positive (CRE) | 2 | 2 | 0 |
| True Positive (Valuation) | 2 | 2 | 0 |
| False Positive Detection | 2 | 2 | 0 |
| **Total** | **8** | **8** | **0** |

---

## 1. True Positive Tests - CNI (Credit Agreement)

### Test 1.1: CNI Credit Agreement
| Attribute | Value |
|-----------|-------|
| File | CNI Credit Agreement (multi-page) |
| Expected Category | CNI |
| Actual Category | CNI |
| Confidence Score | 98% |
| Extraction Status | Successful |
| Fields Extracted | 9 (Borrower, Lender, Facility Amount, Interest Rate, Maturity Date, etc.) |
| DB Save | ✅ Success |
| SME Assignment | ✅ Assigned |
| Blob Routing | ✅ Routed to `cni/` |
| **Result** | **✅ PASS** |

### Test 1.2: CNI Revolving Credit Facility
| Attribute | Value |
|-----------|-------|
| File | Revolving Credit Facility Agreement |
| Expected Category | CNI |
| Actual Category | CNI |
| Confidence Score | 97% |
| Extraction Status | Successful |
| Fields Extracted | 9 (Commitment Amount, Pricing Rules, Payment Frequency, etc.) |
| DB Save | ✅ Success |
| SME Assignment | ✅ Assigned |
| Blob Routing | ✅ Routed to `cni/` |
| **Result** | **✅ PASS** |

---

## 2. True Positive Tests - CRE (Commercial Real Estate Loan)

### Test 2.1: CRE Loan Agreement
| Attribute | Value |
|-----------|-------|
| File | Commercial Real Estate Loan Document |
| Expected Category | CRE |
| Actual Category | CRE |
| Confidence Score | 97% |
| Extraction Status | Successful |
| Fields Extracted | 15 (Relationship Name, Commitment Amount, Interest Rate, Property Address, LTV, DSCR, etc.) |
| DB Save | ✅ Success |
| SME Assignment | ✅ Assigned |
| Blob Routing | ✅ Routed to `cre_valuation/` |
| **Result** | **✅ PASS** |

### Test 2.2: CRE Mortgage Deed
| Attribute | Value |
|-----------|-------|
| File | CRE Mortgage/Deed of Trust |
| Expected Category | CRE |
| Actual Category | CRE |
| Confidence Score | 98% |
| Extraction Status | Successful |
| Fields Extracted | 15 (Property Type, Original Loan Date, Maturity Date, Collateral, etc.) |
| DB Save | ✅ Success |
| SME Assignment | ✅ Assigned |
| Blob Routing | ✅ Routed to `cre_valuation/` |
| **Result** | **✅ PASS** |

---

## 3. True Positive Tests - Valuation (Appraisal Report)

### Test 3.1: Full Appraisal Report
| Attribute | Value |
|-----------|-------|
| File | Summary Appraisal Report (USPAP Compliant) |
| Expected Category | Valuation |
| Actual Category | Valuation |
| Confidence Score | 98% |
| Extraction Status | Successful |
| Fields Extracted | 22 (Subject Property, Appraiser Name, Final Value Conclusion, Effective Date, Comparable Sales, etc.) |
| DB Save | ✅ Success |
| SME Assignment | ✅ Assigned |
| Blob Routing | ✅ Routed to `cre_valuation/` |
| **Result** | **✅ PASS** |

### Test 3.2: Commercial Property Appraisal
| Attribute | Value |
|-----------|-------|
| File | Commercial Property Valuation Report |
| Expected Category | Valuation |
| Actual Category | Valuation |
| Confidence Score | 97% |
| Extraction Status | Successful |
| Fields Extracted | 22 (Income Approach, Sales Comparison, DCF Analysis, Highest and Best Use, etc.) |
| DB Save | ✅ Success |
| SME Assignment | ✅ Assigned |
| Blob Routing | ✅ Routed to `cre_valuation/` |
| **Result** | **✅ PASS** |

---

## 4. False Positive Detection Tests

These tests validate that documents NOT belonging to any trained category are correctly identified and flagged for human intervention.

### Test 4.1: Hotel Invoice (Unrelated Document)
| Attribute | Value |
|-----------|-------|
| File | `mcaps_hotel incoice.pdf` |
| Document Type | Hotel/hospitality invoice |
| Expected Behavior | Reject as "Other" |
| Actual Category | **Other** |
| Confidence Score | **0%** |
| GPT Scoring | Skipped (category is "Other") |
| Extraction | ❌ Skipped |
| DB Save | ❌ Skipped |
| SME Assignment | ❌ Skipped |
| Blob Routing | ❌ Skipped |
| Human Intervention Flag | ✅ `RequiresHumanIntervention = true` |
| **Result** | **✅ PASS - Correctly rejected** |

**Log Evidence:**
```
category=Other, docType=Other, classifierConfidence=0 %, confidenceScore=0%
human intervention required category is unknown for Other (pages 1-2)
Skipping database save and SME assignment because the document requires human intervention
```

### Test 4.2: Property Condition Assessment (Adjacent but Non-matching Document)
| Attribute | Value |
|-----------|-------|
| File | `Property_Condition_Assessment.pdf` |
| Document Type | Engineering/building inspection report |
| Expected Behavior | Reject as "Other" (not an appraisal despite property context) |
| Actual Category | **Other** |
| Confidence Score | **0%** |
| GPT Scoring | Skipped (category is "Other") |
| Extraction | ❌ Skipped |
| DB Save | ❌ Skipped |
| SME Assignment | ❌ Skipped |
| Blob Routing | ❌ Skipped |
| Human Intervention Flag | ✅ `RequiresHumanIntervention = true` |
| **Result** | **✅ PASS - Correctly rejected** |

**Log Evidence:**
```
category=Other, docType=Other, classifierConfidence=0 %, confidenceScore=0%
human intervention required category is unknown for Other (pages 1-1)
Skipping database save and SME assignment because the document requires human intervention
```

---

## Key Observations

### Classifier Precision
- The Azure Content Understanding classifier demonstrates **high precision** — it only assigns CNI/CRE/Valuation when strong domain-specific indicators are present.
- Documents that are tangentially related to a category (e.g., property inspection reports, hotel invoices) are correctly routed to "Other."

### GPT-4.1 Confidence Scoring
- True positive documents consistently score **97-98%** confidence.
- The GPT scoring layer evaluates against domain-specific strong indicators:
  - **Valuation:** USPAP compliance, comparable sales analysis, effective age, remaining economic life, income approach, reconciliation
  - **CRE:** Assignment of Leases and Rents, deed of trust, non-recourse carveouts, DSCR triggers, environmental indemnity
  - **CNI:** Revolving/term loan mechanics, corporate financial covenants, collateral structures

### Human Intervention Triggers
Documents are flagged for human intervention when:
1. Classified as "Other" (0% confidence, no GPT scoring needed)
2. GPT confidence score falls below 70% threshold

### End-to-End Processing Times
| Stage | Average Duration |
|-------|-----------------|
| Classification | 8-15 seconds |
| GPT Confidence Scoring | 3-8 seconds |
| Field Extraction | 8-18 seconds |
| DB Save + SME Assignment | 4-7 seconds |
| Blob Routing | 1-2 seconds |
| **Total (full pipeline)** | **25-50 seconds** |
| **Total (Other/rejected)** | **8-10 seconds** |

---

## Conclusion

All 8 test cases passed successfully. The document classification and extraction system demonstrates:

1. **High accuracy** in correctly classifying documents into their respective categories (CNI, CRE, Valuation)
2. **Effective false positive detection** — documents that don't belong to any trained category are reliably identified and flagged for human review
3. **Robust multi-layer validation** — Azure classifier + GPT-4.1 confidence scoring provides defense-in-depth against misclassification
4. **Complete workflow execution** — successful classifications trigger the full pipeline (extraction → DB save → SME assignment → blob routing)
5. **Graceful rejection** — non-matching documents skip resource-intensive operations and are immediately flagged for human intervention
