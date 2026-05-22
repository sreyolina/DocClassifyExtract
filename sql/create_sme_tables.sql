-- ============================================================
-- SME Round-Robin Assignment Tables
-- Run this script against: genpactpoc database on docstream.database.windows.net
-- ============================================================

-- 1. SME Master Table: stores SME details and capacity
CREATE TABLE dbo.SME (
    SmeId INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    DocType NVARCHAR(50) NOT NULL,         -- 'CRE', 'C&I', 'Valuation'
    MaxConcurrentDocs INT NOT NULL DEFAULT 5,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedDate DATETIME NOT NULL DEFAULT GETDATE()
);

-- 2. Document Assignment Table: tracks which doc is assigned to which SME
CREATE TABLE dbo.DocumentAssignment (
    AssignmentId INT IDENTITY(1,1) PRIMARY KEY,
    DocumentId NVARCHAR(100) NOT NULL,
    SmeId INT NOT NULL,
    DocType NVARCHAR(50) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Assigned',  -- Assigned / Completed / Reassigned
    AssignedDate DATETIME NOT NULL DEFAULT GETUTCDATE(),
    CompletedDate DATETIME NULL,
    CONSTRAINT FK_DocumentAssignment_SME FOREIGN KEY (SmeId) REFERENCES dbo.SME(SmeId)
);

-- 3. Round-Robin Pointer Table: remembers who was assigned last per doc type
CREATE TABLE dbo.RoundRobinPointer (
    DocType NVARCHAR(50) NOT NULL PRIMARY KEY,
    LastAssignedSmeId INT NOT NULL,
    CONSTRAINT FK_RoundRobinPointer_SME FOREIGN KEY (LastAssignedSmeId) REFERENCES dbo.SME(SmeId)
);

-- Indexes for performance
CREATE INDEX IX_DocumentAssignment_SmeId_Status
    ON dbo.DocumentAssignment (SmeId, Status) INCLUDE (AssignedDate);

CREATE INDEX IX_DocumentAssignment_DocumentId
    ON dbo.DocumentAssignment (DocumentId, Status);

CREATE INDEX IX_SME_DocType_Active
    ON dbo.SME (DocType, IsActive) INCLUDE (SmeId, MaxConcurrentDocs);

-- ============================================================
-- Sample data (matching the Excel spreadsheet)
-- ============================================================

INSERT INTO dbo.SME (Name, DocType, MaxConcurrentDocs, IsActive) VALUES
('Prashant',  'CRE',       5, 1),
('Siddharth', 'CRE',       5, 1),
('Sarpreet',  'CRE',       5, 1),
('Nishit',    'C&I',       5, 1),
('Ankit',     'C&I',       5, 0),   -- Inactive
('Sarpreet',  'C&I',       5, 1),
('Sachin',    'C&I',       5, 1),
('Vikas',     'Valuation', 5, 1),
('Apoorv',    'Valuation', 5, 1),
('Sachin',    'Valuation', 5, 1);
