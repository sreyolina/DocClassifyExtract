-- ============================================================================
-- DocClassifyExtract — Database Schema
-- Database: genpactpoc (Azure SQL)
-- Server:   docstream.database.windows.net
-- ============================================================================

-- ────────────────────────────────────────────────────────────────────────────
-- Table: dbo.SME
-- Purpose: Stores Subject Matter Expert details for document review assignment
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE dbo.SME (
    SmeId               INT             IDENTITY(1,1) NOT NULL,
    Name                NVARCHAR(100)   NOT NULL,
    DocType             NVARCHAR(50)    NOT NULL,
    MaxConcurrentDocs   INT             NOT NULL CONSTRAINT DF_SME_MaxConcurrentDocs DEFAULT (5),
    IsActive            BIT             NOT NULL CONSTRAINT DF_SME_IsActive DEFAULT (1),
    CreatedDate         DATETIME        NOT NULL CONSTRAINT DF_SME_CreatedDate DEFAULT (GETDATE()),
    CONSTRAINT PK_SME PRIMARY KEY CLUSTERED (SmeId)
);
GO

-- ────────────────────────────────────────────────────────────────────────────
-- Table: dbo.FeatureRef
-- Purpose: Reference table mapping feature IDs to field names and document types
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE dbo.FeatureRef (
    FeatureId       INT             IDENTITY(1,1) NOT NULL,
    FeatureName     NVARCHAR(255)   NOT NULL,
    DocumentType    NVARCHAR(100)   NULL,
    FieldMethod     NVARCHAR(50)    NULL,
    CONSTRAINT PK_FeatureRef PRIMARY KEY CLUSTERED (FeatureId)
);
GO

-- ────────────────────────────────────────────────────────────────────────────
-- Table: dbo.FeatureData
-- Purpose: Stores extracted field values for each document with citation info
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE dbo.FeatureData (
    Id                          INT             IDENTITY(1,1) NOT NULL,
    DocumentId                  NVARCHAR(500)   NOT NULL,
    FieldName                   NVARCHAR(255)   NULL,
    FeatureId                   INT             NOT NULL,
    Value                       NVARCHAR(MAX)   NULL,
    Confidence                  FLOAT           NOT NULL CONSTRAINT DF_FeatureData_Confidence DEFAULT (0),
    FieldMethod                 NVARCHAR(50)    NULL,
    ConfidenceReason            NVARCHAR(MAX)   NULL,
    ReviewRequired              BIT             NOT NULL CONSTRAINT DF_FeatureData_ReviewRequired DEFAULT (0),
    CitationPageNumber          INT             NULL,
    CitationPdfLink             NVARCHAR(2000)  NULL,
    CitationSpanOffset          INT             NULL,
    CitationSpanLength          INT             NULL,
    CitationHighlightText       NVARCHAR(MAX)   NULL,
    CitationTextMatch           NVARCHAR(MAX)   NULL,
    CitationBoundingBoxX        FLOAT           NULL,
    CitationBoundingBoxY        FLOAT           NULL,
    CitationBoundingBoxWidth    FLOAT           NULL,
    CitationBoundingBoxHeight   FLOAT           NULL,
    CreatedDate                 DATETIME        NOT NULL CONSTRAINT DF_FeatureData_CreatedDate DEFAULT (GETDATE()),
    CreateUser                  INT             NOT NULL CONSTRAINT DF_FeatureData_CreateUser DEFAULT (999),
    ModifiedDate                DATETIME        NOT NULL CONSTRAINT DF_FeatureData_ModifiedDate DEFAULT (GETDATE()),
    ModifyUser                  INT             NOT NULL CONSTRAINT DF_FeatureData_ModifyUser DEFAULT (999),
    CONSTRAINT PK_FeatureData PRIMARY KEY CLUSTERED (Id)
);
GO

-- ────────────────────────────────────────────────────────────────────────────
-- Table: dbo.JobDetails
-- Purpose: Tracks document processing job status
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE dbo.JobDetails (
    JobId       NVARCHAR(100)   NOT NULL,
    DocumentId  NVARCHAR(500)   NULL,
    Status      NVARCHAR(100)   NULL,
    CONSTRAINT PK_JobDetails PRIMARY KEY CLUSTERED (JobId)
);
GO

-- ────────────────────────────────────────────────────────────────────────────
-- Table: dbo.DocumentAssignment
-- Purpose: Tracks which SME is assigned to review each document
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE dbo.DocumentAssignment (
    AssignmentId    INT             IDENTITY(1,1) NOT NULL,
    DocumentId      NVARCHAR(100)   NOT NULL,
    SmeId           INT             NOT NULL,
    DocType         NVARCHAR(50)    NOT NULL,
    Status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_DocumentAssignment_Status DEFAULT ('Assigned'),
    AssignedDate    DATETIME        NOT NULL CONSTRAINT DF_DocumentAssignment_AssignedDate DEFAULT (GETUTCDATE()),
    CompletedDate   DATETIME        NULL,
    CONSTRAINT PK_DocumentAssignment PRIMARY KEY CLUSTERED (AssignmentId),
    CONSTRAINT FK_DocumentAssignment_SME FOREIGN KEY (SmeId) REFERENCES dbo.SME (SmeId)
);
GO

-- ────────────────────────────────────────────────────────────────────────────
-- Table: dbo.RoundRobinPointer
-- Purpose: Tracks last assigned SME per document type for round-robin rotation
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE dbo.RoundRobinPointer (
    DocType             NVARCHAR(50)    NOT NULL,
    LastAssignedSmeId   INT             NOT NULL,
    CONSTRAINT PK_RoundRobinPointer PRIMARY KEY CLUSTERED (DocType),
    CONSTRAINT FK_RoundRobinPointer_SME FOREIGN KEY (LastAssignedSmeId) REFERENCES dbo.SME (SmeId)
);
GO
