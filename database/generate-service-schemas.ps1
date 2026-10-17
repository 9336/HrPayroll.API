<#
  Builds one schema script per service (database\services\<Service>.sql) from the single-database script
  ..\..\database\HrPayroll_Database.sql: each service gets only the tables it owns, foreign keys to tables owned by
  other services are dropped (those ids now point into another database), and the read-only copies of the company
  directory plus the Kafka outbox / inbox tables are added.

  Run it again after changing HrPayroll_Database.sql:  powershell -File database\generate-service-schemas.ps1
#>
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here '..\..\database\HrPayroll_Database.sql'
$outDir = Join-Path $here 'services'
New-Item -ItemType Directory -Force $outDir | Out-Null

# ------------------------------------------------------------------ Who owns which table
$owned = [ordered]@{
    Identity      = @('Roles', 'RolePermissions', 'Users', 'RefreshTokens')
    Employees     = @('Departments', 'Employees', 'CompanyProfile', 'Holidays', 'EmployeePhotos', 'EmployeeUploads', 'EmployeeUploadFiles')
    Leave         = @('LeaveTypes', 'LeaveBalances', 'LeaveRequests', 'LeaveYearRollovers')
    Attendance    = @('Attendance', 'AttendanceLocations', 'AttendanceLocationEmployees', 'AttendanceSettings', 'AttendanceDevices', 'EmployeeFaces', 'AttendanceSelfies', 'WorkLogs')
    Payroll       = @('SalaryStructures', 'Payslips', 'EmployeeDocuments', 'ClosedFinancialYears', 'Promotions')
    Expenses      = @('ExpenseCategories', 'OfficeExpenses', 'TravelClaims', 'TravelClaimItems', 'TravelReceipts', 'TravelClaimEvents')
    Performance   = @('AppraisalCycles', 'AppraisalQuestions', 'Appraisals', 'AppraisalAnswers', 'EmployeeFeedback', 'FeedbackAttachments', 'WorkTasks', 'WorkTaskUpdates')
    Recruitment   = @('JobOpenings', 'Candidates', 'CandidateResumes', 'JobApplications', 'Interviews', 'RecruitmentActivities', 'QuestionBanks', 'BankQuestions', 'CandidateTests', 'CandidateTestQuestions')
    Helpdesk      = @('ItRequests', 'ItRequestEvents', 'Visits')
    Chat          = @('ChatConversations', 'ChatMembers', 'ChatMessages', 'ChatAttachments')
    Notifications = @('EmailMessages', 'EmailSettings')
}
# Extra copies some services keep (besides the company directory, which every service has).
$extraCopies = @{
    Attendance = @('LeaveRecords')
    Payroll    = @('LeaveRecords')
    Employees  = @('SalarySnapshots')
}

# ------------------------------------------------------------------ Read the single-database script
$batches = ([IO.File]::ReadAllText($source) -replace "`r`n", "`n") -split "(?m)^\s*GO\s*$"
$tableDdl = @{}; $indexDdl = @{}; $programmability = @()
foreach ($b in $batches) {
    $text = $b.Trim()
    if ($text -match '(?m)^\s*CREATE TABLE dbo\.(\w+)') { $tableDdl[$Matches[1]] = $text; continue }
    if ($text -match '^(--[^\n]*\n)*\s*CREATE (UNIQUE )?INDEX \w+ ON dbo\.(\w+)') {
        $t = $Matches[3]; if (-not $indexDdl[$t]) { $indexDdl[$t] = @() }; $indexDdl[$t] += $text; continue
    }
    if ($text -match 'CREATE (VIEW|PROCEDURE) dbo\.(\w+)') { $programmability += , @($Matches[2], $text) }
}

function Strip-ForeignKeys([string]$ddl, [string[]]$keep) {
    [regex]::Replace($ddl, '\s*CONSTRAINT\s+FK_\w+\s+REFERENCES\s+dbo\.(\w+)\s*\(\w+\)(\s+ON DELETE (CASCADE|SET NULL))?', {
        param($m) if ($keep -contains $m.Groups[1].Value) { $m.Value } else { '' } })
}

# ------------------------------------------------------------------ Copies (no identity, no foreign keys: the owner decides)
$copyDdl = @{
    Departments = @'
CREATE TABLE dbo.Departments
(
    DepartmentId INT           NOT NULL CONSTRAINT PK_Departments PRIMARY KEY,
    Name         NVARCHAR(100) NOT NULL,
    IsActive     BIT           NOT NULL CONSTRAINT DF_Departments_IsActive DEFAULT (1)
);
'@
    Employees = @'
CREATE TABLE dbo.Employees
(
    EmployeeId            INT           NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
    EmployeeCode          NVARCHAR(20)  NOT NULL,
    FirstName             NVARCHAR(100) NOT NULL,
    LastName              NVARCHAR(100) NOT NULL,
    Email                 NVARCHAR(200) NOT NULL,
    Phone                 NVARCHAR(20)  NULL,
    Gender                CHAR(1)       NULL,
    DateOfBirth           DATE          NULL,
    DateOfJoining         DATE          NOT NULL,
    DepartmentId          INT           NOT NULL,
    Designation           NVARCHAR(100) NOT NULL,
    ManagerId             INT           NULL,{{FINANCIAL}}
    BloodGroup            VARCHAR(3)    NULL,
    EmergencyContactName  NVARCHAR(100) NULL,
    EmergencyContactPhone NVARCHAR(20)  NULL,
    IsActive              BIT           NOT NULL,
    CreatedAt             DATETIME2(0)  NOT NULL,
    UpdatedAt             DATETIME2(0)  NULL
);
CREATE INDEX IX_Employees_ManagerId ON dbo.Employees(ManagerId);
'@
    Holidays = @'
CREATE TABLE dbo.Holidays
(
    HolidayId   INT           NOT NULL CONSTRAINT PK_Holidays PRIMARY KEY,
    HolidayDate DATE          NOT NULL,
    Name        NVARCHAR(100) NOT NULL
);
'@
    CompanyProfile = @'
CREATE TABLE dbo.CompanyProfile
(
    CompanyId            INT           NOT NULL CONSTRAINT PK_CompanyProfile PRIMARY KEY,
    Name                 NVARCHAR(150) NOT NULL,
    LegalName            NVARCHAR(200) NULL,
    AddressLine1         NVARCHAR(200) NULL,
    AddressLine2         NVARCHAR(200) NULL,
    City                 NVARCHAR(100) NULL,
    State                NVARCHAR(100) NULL,
    PinCode              NVARCHAR(10)  NULL,
    Phone                NVARCHAR(20)  NULL,
    Email                NVARCHAR(200) NULL,
    Website              NVARCHAR(200) NULL,
    Pan                  NVARCHAR(10)  NULL,
    Tan                  NVARCHAR(10)  NULL,
    Gstin                NVARCHAR(15)  NULL,
    SignatoryName        NVARCHAR(150) NULL,
    SignatoryDesignation NVARCHAR(100) NULL,
    UpdatedAt            DATETIME2(0)  NULL
);
'@
    Roles = @'
CREATE TABLE dbo.Roles
(
    RoleId      INT           NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    Name        NVARCHAR(50)  NOT NULL,
    Description NVARCHAR(200) NULL,
    IsSystem    BIT           NOT NULL CONSTRAINT DF_Roles_IsSystem DEFAULT (0)
);
'@
    RolePermissions = @'
CREATE TABLE dbo.RolePermissions
(
    RoleId     INT          NOT NULL,
    Permission NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, Permission)
);
'@
    Users = @'
-- No password hashes outside the Identity service. LastSeenAt is written by the Chat service only.
CREATE TABLE dbo.Users
(
    UserId      INT          NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Username    NVARCHAR(50) NOT NULL,
    RoleId      INT          NOT NULL,
    EmployeeId  INT          NULL,
    IsActive    BIT          NOT NULL,
    LastLoginAt DATETIME2(0) NULL,
    LastSeenAt  DATETIME2(0) NULL,
    CreatedAt   DATETIME2(0) NOT NULL
);
CREATE INDEX IX_Users_EmployeeId ON dbo.Users(EmployeeId);
'@
    LeaveRecords = @'
-- Copy of the Leave service's requests (hr.leave-requests).
CREATE TABLE dbo.LeaveRecords
(
    LeaveRequestId INT           NOT NULL CONSTRAINT PK_LeaveRecords PRIMARY KEY,
    EmployeeId     INT           NOT NULL,
    LeaveTypeId    INT           NOT NULL,
    LeaveCode      NVARCHAR(10)  NOT NULL,
    LeaveName      NVARCHAR(100) NOT NULL,
    IsPaid         BIT           NOT NULL,
    FromDate       DATE          NOT NULL,
    ToDate         DATE          NOT NULL,
    IsHalfDay      BIT           NOT NULL,
    TotalDays      DECIMAL(5,1)  NOT NULL,
    Status         NVARCHAR(20)  NOT NULL
);
CREATE INDEX IX_LeaveRecords_Employee_Dates ON dbo.LeaveRecords(EmployeeId, FromDate, ToDate) INCLUDE (Status);
'@
    SalarySnapshots = @'
-- Copy of the Payroll service's salary revisions (hr.salaries): only the monthly gross.
CREATE TABLE dbo.SalarySnapshots
(
    SalaryStructureId INT           NOT NULL CONSTRAINT PK_SalarySnapshots PRIMARY KEY,
    EmployeeId        INT           NOT NULL,
    EffectiveFrom     DATE          NOT NULL,
    GrossMonthly      DECIMAL(12,2) NOT NULL
);
CREATE INDEX IX_SalarySnapshots_Employee ON dbo.SalarySnapshots(EmployeeId, EffectiveFrom);
'@
}

$messaging = @'
/* Kafka transactional outbox: events saved with the business change, published by the service's relay. */
CREATE TABLE dbo.OutboxMessages
(
    Id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
    MessageId   UNIQUEIDENTIFIER     NOT NULL,
    Topic       VARCHAR(200)         NOT NULL,
    MessageKey  NVARCHAR(200)        NOT NULL,
    EventType   VARCHAR(200)         NOT NULL,
    Payload     NVARCHAR(MAX)        NOT NULL,
    OccurredAt  DATETIME2            NOT NULL,
    SentAt      DATETIME2            NULL,
    Attempts    INT                  NOT NULL CONSTRAINT DF_OutboxMessages_Attempts DEFAULT (0),
    LastError   NVARCHAR(1000)       NULL
);
CREATE INDEX IX_OutboxMessages_Unsent ON dbo.OutboxMessages(Id) WHERE SentAt IS NULL;
GO

/* Kafka messages this service has processed (at-least-once delivery: repeats are skipped). */
CREATE TABLE dbo.InboxMessages
(
    MessageId   UNIQUEIDENTIFIER NOT NULL,
    Consumer    VARCHAR(100)     NOT NULL,
    ProcessedAt DATETIME2        NOT NULL,
    CONSTRAINT PK_InboxMessages PRIMARY KEY (MessageId, Consumer)
);
GO
'@

# ------------------------------------------------------------------ Write one script per service
$directoryTables = @('Departments', 'Employees', 'Holidays', 'CompanyProfile', 'Roles', 'RolePermissions', 'Users')
foreach ($svc in $owned.Keys) {
    $db = '$(DbName)'   # sqlcmd variable: setup-databases.ps1 passes -v DbName=HrPayroll_<Service>
    $mine = $owned[$svc]
    $sb = [Text.StringBuilder]::new()
    [void]$sb.AppendLine("/* =====================================================================")
    [void]$sb.AppendLine("   Database of the $svc service (generated by generate-service-schemas.ps1). Run with -v DbName=HrPayroll_$svc")
    [void]$sb.AppendLine("   Owned tables: $($mine -join ', ')")
    [void]$sb.AppendLine("   ===================================================================== */")
    [void]$sb.AppendLine("IF DB_ID(N'$db') IS NOT NULL BEGIN PRINT '$db already exists - nothing to do.'; SET NOEXEC ON; END")
    [void]$sb.AppendLine("GO")
    [void]$sb.AppendLine("CREATE DATABASE [$db];")
    [void]$sb.AppendLine("GO")
    [void]$sb.AppendLine("USE [$db];")
    [void]$sb.AppendLine("GO")
    [void]$sb.AppendLine("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;")
    [void]$sb.AppendLine("GO")
    [void]$sb.AppendLine($messaging)

    $copies = @($directoryTables | Where-Object { $mine -notcontains $_ }) + @($extraCopies[$svc] | Where-Object { $_ })
    if ($copies.Count -gt 0) {
        [void]$sb.AppendLine("/* ---------------------------------------------------------------------")
        [void]$sb.AppendLine("   Read-only copies, kept up to date from Kafka")
        [void]$sb.AppendLine("   --------------------------------------------------------------------- */")
        foreach ($c in $copies) {
            $ddl = $copyDdl[$c]
            if ($c -eq 'Employees') {
                $financial = if ($svc -eq 'Payroll') { "`n    PanNumber             NVARCHAR(10)  NULL,`n    BankName              NVARCHAR(100) NULL,`n    BankAccountNo         NVARCHAR(30)  NULL,`n    IfscCode              NVARCHAR(11)  NULL," } else { '' }
                $ddl = $ddl.Replace('{{FINANCIAL}}', $financial)
            }
            [void]$sb.AppendLine($ddl.Trim()); [void]$sb.AppendLine("GO")
        }
    }

    [void]$sb.AppendLine("/* ---------------------------------------------------------------------")
    [void]$sb.AppendLine("   Tables owned by the $svc service")
    [void]$sb.AppendLine("   --------------------------------------------------------------------- */")
    foreach ($t in $mine) {
        if (-not $tableDdl[$t]) { throw "No CREATE TABLE for $t in $source" }
        [void]$sb.AppendLine((Strip-ForeignKeys $tableDdl[$t] $mine)); [void]$sb.AppendLine("GO")
        foreach ($i in @($indexDdl[$t] | Where-Object { $_ })) { [void]$sb.AppendLine($i); [void]$sb.AppendLine("GO") }
    }
    if ($svc -eq 'Leave') {
        foreach ($p in $programmability) { [void]$sb.AppendLine($p[1]); [void]$sb.AppendLine("GO") }
    }
    [void]$sb.AppendLine("SET NOEXEC OFF;")
    [void]$sb.AppendLine("GO")
    [IO.File]::WriteAllText((Join-Path $outDir "$svc.sql"), ($sb.ToString() -replace "`r?`n", "`r`n"), [Text.UTF8Encoding]::new($true))
    Write-Host "  $svc.sql  ($($mine.Count) tables, $($copies.Count) copies)"
}
