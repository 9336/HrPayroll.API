/* =====================================================================
   One-time data move: the single database (HrPayrollDb) -> one database per service.

   Run by setup-databases.ps1 after the service databases exist, or by hand:
       sqlcmd -S . -E -i migrate-from-single-database.sql -v SourceDb=HrPayrollDb Prefix=HrPayroll_
   Safe to run again: a table that already has rows is left alone.

   Each service gets the rows of the tables it owns, plus its read-only copies of the company directory (employees,
   departments, holidays, company, logins without passwords, roles) and, where needed, of leave requests / salaries.
   The single database is not changed.
   ===================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* Copies the columns both tables have (computed columns excluded), keeping the ids. */
CREATE OR ALTER PROCEDURE #CopyTable @Db sysname, @Table sysname
AS
BEGIN
    DECLARE @exec nvarchar(400) = QUOTENAME(@Db) + N'.sys.sp_executesql';
    DECLARE @cols nvarchar(max), @identity bit, @rows int;

    EXEC @exec N'
        SELECT @cols = STUFF((SELECT N'','' + QUOTENAME(c.name)
                              FROM sys.columns c
                              WHERE c.object_id = OBJECT_ID(@t) AND c.is_computed = 0
                                AND COL_LENGTH(N''$(SourceDb).dbo.'' + @t, c.name) IS NOT NULL
                              ORDER BY c.column_id FOR XML PATH(''''), TYPE).value(''.'', ''nvarchar(max)''), 1, 1, N''''),
               @identity = OBJECTPROPERTY(OBJECT_ID(@t), ''TableHasIdentity''),
               @rows = (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(@t) AND p.index_id IN (0, 1));',
        N'@t sysname, @cols nvarchar(max) OUTPUT, @identity bit OUTPUT, @rows int OUTPUT',
        @t = @Table, @cols = @cols OUTPUT, @identity = @identity OUTPUT, @rows = @rows OUTPUT;

    IF @cols IS NULL BEGIN RAISERROR(N'%s.dbo.%s not found', 16, 1, @Db, @Table); RETURN; END
    IF @rows > 0 BEGIN PRINT CONCAT(@Db, N'.', @Table, N': already has rows - skipped'); RETURN; END

    DECLARE @sql nvarchar(max) =
        CASE WHEN @identity = 1 THEN N'SET IDENTITY_INSERT dbo.' + QUOTENAME(@Table) + N' ON; ' ELSE N'' END +
        N'INSERT INTO dbo.' + QUOTENAME(@Table) + N' (' + @cols + N') SELECT ' + @cols + N' FROM $(SourceDb).dbo.' + QUOTENAME(@Table) + N'; ' +
        N'PRINT CONCAT(DB_NAME(), N''.'', ''' + @Table + N''', N'': '', @@ROWCOUNT, N'' row(s)'');' +
        CASE WHEN @identity = 1 THEN N' SET IDENTITY_INSERT dbo.' + QUOTENAME(@Table) + N' OFF;' ELSE N'' END;
    EXEC @exec @sql;
END
GO

/* The company directory copy every service keeps (the owners get their own tables below). */
CREATE OR ALTER PROCEDURE #CopyDirectory @Db sysname, @SkipEmployees bit = 0, @SkipUsers bit = 0
AS
BEGIN
    IF @SkipEmployees = 0
    BEGIN
        EXEC #CopyTable @Db, N'Departments';
        EXEC #CopyTable @Db, N'Employees';
        EXEC #CopyTable @Db, N'Holidays';
        EXEC #CopyTable @Db, N'CompanyProfile';
    END
    IF @SkipUsers = 0
    BEGIN
        EXEC #CopyTable @Db, N'Roles';
        EXEC #CopyTable @Db, N'RolePermissions';
        EXEC #CopyTable @Db, N'Users';
    END
END
GO

PRINT '--- Identity';
EXEC #CopyDirectory N'$(Prefix)Identity', @SkipUsers = 1;
EXEC #CopyTable N'$(Prefix)Identity', N'Roles';
EXEC #CopyTable N'$(Prefix)Identity', N'RolePermissions';
EXEC #CopyTable N'$(Prefix)Identity', N'Users';
EXEC #CopyTable N'$(Prefix)Identity', N'RefreshTokens';
GO

PRINT '--- Employees';
EXEC #CopyDirectory N'$(Prefix)Employees', @SkipEmployees = 1;
EXEC #CopyTable N'$(Prefix)Employees', N'Departments';
EXEC #CopyTable N'$(Prefix)Employees', N'Employees';
EXEC #CopyTable N'$(Prefix)Employees', N'CompanyProfile';
EXEC #CopyTable N'$(Prefix)Employees', N'Holidays';
EXEC #CopyTable N'$(Prefix)Employees', N'EmployeePhotos';
EXEC #CopyTable N'$(Prefix)Employees', N'EmployeeUploads';
EXEC #CopyTable N'$(Prefix)Employees', N'EmployeeUploadFiles';
IF NOT EXISTS (SELECT 1 FROM [$(Prefix)Employees].dbo.SalarySnapshots)
    INSERT INTO [$(Prefix)Employees].dbo.SalarySnapshots (SalaryStructureId, EmployeeId, EffectiveFrom, GrossMonthly)
    SELECT SalaryStructureId, EmployeeId, EffectiveFrom, GrossMonthly FROM $(SourceDb).dbo.SalaryStructures;
GO

PRINT '--- Leave';
EXEC #CopyDirectory N'$(Prefix)Leave';
EXEC #CopyTable N'$(Prefix)Leave', N'LeaveTypes';
EXEC #CopyTable N'$(Prefix)Leave', N'LeaveBalances';
EXEC #CopyTable N'$(Prefix)Leave', N'LeaveRequests';
EXEC #CopyTable N'$(Prefix)Leave', N'LeaveYearRollovers';
GO

/* Copy of leave requests for Attendance and Payroll. */
CREATE OR ALTER PROCEDURE #CopyLeaveRecords @Db sysname
AS
BEGIN
    DECLARE @exec nvarchar(400) = QUOTENAME(@Db) + N'.sys.sp_executesql';
    EXEC @exec N'
        IF NOT EXISTS (SELECT 1 FROM dbo.LeaveRecords)
            INSERT INTO dbo.LeaveRecords (LeaveRequestId, EmployeeId, LeaveTypeId, LeaveCode, LeaveName, IsPaid, FromDate, ToDate, IsHalfDay, TotalDays, Status)
            SELECT r.LeaveRequestId, r.EmployeeId, r.LeaveTypeId, t.Code, t.Name, t.IsPaid, r.FromDate, r.ToDate, r.IsHalfDay, r.TotalDays, r.Status
            FROM $(SourceDb).dbo.LeaveRequests r JOIN $(SourceDb).dbo.LeaveTypes t ON t.LeaveTypeId = r.LeaveTypeId;';
END
GO

PRINT '--- Attendance';
EXEC #CopyDirectory N'$(Prefix)Attendance';
EXEC #CopyLeaveRecords N'$(Prefix)Attendance';
EXEC #CopyTable N'$(Prefix)Attendance', N'Attendance';
EXEC #CopyTable N'$(Prefix)Attendance', N'AttendanceLocations';
EXEC #CopyTable N'$(Prefix)Attendance', N'AttendanceLocationEmployees';

EXEC #CopyTable N'$(Prefix)Attendance', N'AttendanceSettings';
EXEC #CopyTable N'$(Prefix)Attendance', N'AttendanceDevices';
EXEC #CopyTable N'$(Prefix)Attendance', N'EmployeeFaces';
EXEC #CopyTable N'$(Prefix)Attendance', N'AttendanceSelfies';
EXEC #CopyTable N'$(Prefix)Attendance', N'WorkLogs';
GO

PRINT '--- Payroll';
EXEC #CopyDirectory N'$(Prefix)Payroll';
EXEC #CopyLeaveRecords N'$(Prefix)Payroll';
EXEC #CopyTable N'$(Prefix)Payroll', N'SalaryStructures';
EXEC #CopyTable N'$(Prefix)Payroll', N'Payslips';
EXEC #CopyTable N'$(Prefix)Payroll', N'EmployeeDocuments';
EXEC #CopyTable N'$(Prefix)Payroll', N'ClosedFinancialYears';
EXEC #CopyTable N'$(Prefix)Payroll', N'Promotions';
GO

PRINT '--- Expenses';
EXEC #CopyDirectory N'$(Prefix)Expenses';
EXEC #CopyTable N'$(Prefix)Expenses', N'ExpenseCategories';
EXEC #CopyTable N'$(Prefix)Expenses', N'OfficeExpenses';
EXEC #CopyTable N'$(Prefix)Expenses', N'TravelClaims';
EXEC #CopyTable N'$(Prefix)Expenses', N'TravelClaimItems';
EXEC #CopyTable N'$(Prefix)Expenses', N'TravelReceipts';
EXEC #CopyTable N'$(Prefix)Expenses', N'TravelClaimEvents';
GO

PRINT '--- Performance';
EXEC #CopyDirectory N'$(Prefix)Performance';
EXEC #CopyTable N'$(Prefix)Performance', N'AppraisalCycles';
EXEC #CopyTable N'$(Prefix)Performance', N'AppraisalQuestions';
EXEC #CopyTable N'$(Prefix)Performance', N'Appraisals';
EXEC #CopyTable N'$(Prefix)Performance', N'AppraisalAnswers';
EXEC #CopyTable N'$(Prefix)Performance', N'EmployeeFeedback';
EXEC #CopyTable N'$(Prefix)Performance', N'FeedbackAttachments';
EXEC #CopyTable N'$(Prefix)Performance', N'WorkTasks';
EXEC #CopyTable N'$(Prefix)Performance', N'WorkTaskUpdates';
GO

PRINT '--- Recruitment';
EXEC #CopyDirectory N'$(Prefix)Recruitment';
EXEC #CopyTable N'$(Prefix)Recruitment', N'JobOpenings';
EXEC #CopyTable N'$(Prefix)Recruitment', N'Candidates';
EXEC #CopyTable N'$(Prefix)Recruitment', N'CandidateResumes';
EXEC #CopyTable N'$(Prefix)Recruitment', N'JobApplications';
EXEC #CopyTable N'$(Prefix)Recruitment', N'Interviews';
EXEC #CopyTable N'$(Prefix)Recruitment', N'RecruitmentActivities';
EXEC #CopyTable N'$(Prefix)Recruitment', N'QuestionBanks';
EXEC #CopyTable N'$(Prefix)Recruitment', N'BankQuestions';
EXEC #CopyTable N'$(Prefix)Recruitment', N'CandidateTests';
EXEC #CopyTable N'$(Prefix)Recruitment', N'CandidateTestQuestions';
GO

PRINT '--- Helpdesk';
EXEC #CopyDirectory N'$(Prefix)Helpdesk';
EXEC #CopyTable N'$(Prefix)Helpdesk', N'ItRequests';
EXEC #CopyTable N'$(Prefix)Helpdesk', N'ItRequestEvents';
EXEC #CopyTable N'$(Prefix)Helpdesk', N'Visits';
GO

PRINT '--- Chat';
EXEC #CopyDirectory N'$(Prefix)Chat';
EXEC #CopyTable N'$(Prefix)Chat', N'ChatConversations';
EXEC #CopyTable N'$(Prefix)Chat', N'ChatMembers';
EXEC #CopyTable N'$(Prefix)Chat', N'ChatMessages';
EXEC #CopyTable N'$(Prefix)Chat', N'ChatAttachments';
GO

PRINT '--- Notifications';
EXEC #CopyDirectory N'$(Prefix)Notifications';
EXEC #CopyTable N'$(Prefix)Notifications', N'EmailMessages';
EXEC #CopyTable N'$(Prefix)Notifications', N'EmailSettings';
GO

PRINT 'Done.';
GO
