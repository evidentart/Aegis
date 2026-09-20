using Aegis;
using Aegis.Core;
using Aegis.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aegis.Tests;

public sealed class PerformancePersistenceTests
{
    [Fact]
    public async Task PersistsAndReconstructsStructuredInvestigationReport()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("step-1", WindowsSystemInfoObservationTool.ToolId));
        var systemInfo = new WindowsSystemInfo("Windows", "10.0", 26100, "X64");
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution("step-1", WindowsSystemInfoObservationTool.ToolId, systemInfo));
        var report = new InvestigationReport(
            "The system was observed.",
            [new EvidenceStatement("Windows was observed.", ["step-1"])],
            [new EvidenceStatement("The observation supports this interpretation.", ["step-1"])],
            [],
            ["This is a point-in-time observation."],
            ["Review the evidence before taking action."]);

        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: report.Summary, Report: report),
            DateTimeOffset.UtcNow));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        Assert.Equal(report.Summary, details!.Investigation.Outcome!.Report!.Summary);
        Assert.Equal("step-1", Assert.Single(details.Investigation.Outcome.Report.ObservedFacts).EvidenceStepIds.Single());
        Assert.Equal("The observation supports this interpretation.",
            Assert.Single(details.Investigation.Outcome.Report.Conclusions).Text);
        Assert.Equal(4, fixture.CountRows("InvestigationReportStatements"));
        Assert.Equal(2, fixture.CountRows("InvestigationReportEvidenceReferences"));
    }

    [Fact]
    public async Task LegacyFinalAnswerReconstructsAsSummaryOnlyReport()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("step-1", WindowsSystemInfoObservationTool.ToolId));

        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Legacy answer."),
            DateTimeOffset.UtcNow));

        var report = (await fixture.Store.GetAsync(fixture.InvestigationId))!.Investigation.Outcome!.Report!;

        Assert.Equal("Legacy answer.", report.Summary);
        Assert.Empty(report.ObservedFacts);
        Assert.Empty(report.Conclusions);
        Assert.Empty(report.Hypotheses);
        Assert.Empty(report.Uncertainties);
        Assert.Empty(report.Recommendations);
        Assert.Equal(0, fixture.CountRows("InvestigationReportStatements"));
    }

    [Fact]
    public async Task InvalidStructuredReportRollsBackTerminalFinalization()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("step-1", WindowsSystemInfoObservationTool.ToolId));
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(
                "step-1",
                WindowsSystemInfoObservationTool.ToolId,
                new WindowsSystemInfo("Windows", "10.0", 26100, "X64")));
        var invalidReport = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Fact", ["not-collected"])],
            [],
            [],
            [],
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: invalidReport.Summary, Report: invalidReport),
            DateTimeOffset.UtcNow));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);
        Assert.Equal(InvestigationLifecycleStatus.Running, details!.Investigation.LifecycleStatus);
        Assert.Null(details.Investigation.Outcome);
        Assert.Equal(0, fixture.CountRows("InvestigationReportStatements"));
    }

    [Fact]
    public async Task RejectsInvalidPersistedReportSectionKind()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("step-1", WindowsSystemInfoObservationTool.ToolId));
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO InvestigationReportStatements
                (InvestigationId, SectionKind, StatementOrdinal, Text)
            VALUES ($investigation, 'Invalid', 0, 'unexpected');
            """;
        command.Parameters.AddWithValue("$investigation", fixture.InvestigationId.ToString("D"));

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task PersistsAndReconstructsRecentErrorEventsAndEvidenceReferences()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("events", WindowsRecentErrorEventsObservationTool.ToolId));
        var data = CreateRecentErrorEvents(isTruncated: true);
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution("events", WindowsRecentErrorEventsObservationTool.ToolId, data));
        var report = new InvestigationReport(
            "Recent Windows failures were observed.",
            [new EvidenceStatement("Windows recorded recent failures.", ["events"])],
            [],
            [],
            ["The event metadata does not establish causation."],
            []);

        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: report.Summary, Report: report),
            DateTimeOffset.UtcNow));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);
        var executionData = Assert.IsType<WindowsRecentErrorEvents>(
            Assert.Single(details!.Investigation.StepExecutions).Result!.Data);
        Assert.True(executionData.IsTruncated);
        Assert.Equal(2, executionData.Events.Count);
        Assert.Equal(WindowsEventChannelKind.Application, executionData.Events[0].Channel);
        Assert.Equal(WindowsEventSeverity.Critical, executionData.Events[0].Severity);
        Assert.Equal("Application.Error", executionData.Events[0].ProviderName);
        Assert.Equal("events", Assert.Single(details.Investigation.Outcome!.Report!.ObservedFacts).EvidenceStepIds.Single());
        Assert.Equal(1, fixture.CountRows("WindowsRecentErrorEventSnapshots"));
        Assert.Equal(2, fixture.CountRows("WindowsRecentErrorEventEntries"));
    }

    [Fact]
    public async Task RecentErrorEventToolMismatchRollsBackAllObservationRows()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("events", WindowsRecentErrorEventsObservationTool.ToolId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(
                "events",
                WindowsRecentErrorEventsObservationTool.ToolId,
                new WindowsSystemInfo("Windows", "10.0", 26100, "X64"))));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("WindowsRecentErrorEventSnapshots"));
        Assert.Equal(0, fixture.CountRows("WindowsRecentErrorEventEntries"));
    }

    [Theory]
    [InlineData("ChannelKind", "Security")]
    [InlineData("Severity", "Warning")]
    public async Task RejectsMalformedPersistedRecentErrorClosedValues(string column, string value)
    {
        using var fixture = await CreateEventFixtureAsync();
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA ignore_check_constraints = ON;
            UPDATE WindowsRecentErrorEventEntries
            SET {column} = $value
            WHERE ObservationId = (SELECT ObservationId FROM Observations WHERE StepId = 'events');
            """;
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() => fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task RejectsMalformedPersistedRecentErrorOrdinals()
    {
        using var fixture = await CreateEventFixtureAsync();
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA ignore_check_constraints = ON;
            UPDATE WindowsRecentErrorEventEntries
            SET Ordinal = 2
            WHERE ObservationId = (SELECT ObservationId FROM Observations WHERE StepId = 'events')
                AND Ordinal = 1;
            """;
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() => fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task RejectsExcessivePersistedRecentErrorRows()
    {
        using var fixture = await CreateEventFixtureAsync();
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA ignore_check_constraints = ON;
            INSERT INTO WindowsRecentErrorEventEntries
                (ObservationId, Ordinal, ChannelKind, ProviderName, EventId, Severity, OccurredAtUtc)
            VALUES ($observation, $ordinal, 'System', 'System.Error', 100, 'Error', $occurred);
            """;
        command.Parameters.AddWithValue("$observation", 1);
        command.Parameters.AddWithValue("$ordinal", 2);
        command.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O"));
        for (var ordinal = 2; ordinal <= 21; ordinal++)
        {
            command.Parameters["$ordinal"].Value = ordinal;
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() => fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task RejectsNonContiguousPersistedReportStatementOrdinals()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("step-1", WindowsSystemInfoObservationTool.ToolId));
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(
                "step-1",
                WindowsSystemInfoObservationTool.ToolId,
                new WindowsSystemInfo("Windows", "10.0", 26100, "X64")));
        var report = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Fact", ["step-1"])],
            [],
            [],
            ["Uncertainty one", "Uncertainty two"],
            []);

        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: report.Summary, Report: report),
            DateTimeOffset.UtcNow));

        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE InvestigationReportStatements
                SET StatementOrdinal = 2
                WHERE InvestigationId = $investigation AND SectionKind = 'Uncertainties'
                    AND StatementOrdinal = 1;
                """;
            command.Parameters.AddWithValue("$investigation", fixture.InvestigationId.ToString("D"));
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() => fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task RejectsNonContiguousPersistedEvidenceReferenceOrdinals()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("step-1", WindowsSystemInfoObservationTool.ToolId),
            ("step-2", WindowsSystemInfoObservationTool.ToolId));
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(
                "step-1",
                WindowsSystemInfoObservationTool.ToolId,
                new WindowsSystemInfo("Windows", "10.0", 26100, "X64")));
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(
                "step-2",
                WindowsSystemInfoObservationTool.ToolId,
                new WindowsSystemInfo("Windows", "10.0", 26100, "X64")));
        var report = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Fact", ["step-1", "step-2"])],
            [],
            [],
            [],
            []);

        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: report.Summary, Report: report),
            DateTimeOffset.UtcNow));

        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE InvestigationReportEvidenceReferences
                SET ReferenceOrdinal = 2
                WHERE InvestigationId = $investigation AND SectionKind = 'ObservedFacts'
                    AND StatementOrdinal = 0 AND ReferenceOrdinal = 1;
                """;
            command.Parameters.AddWithValue("$investigation", fixture.InvestigationId.ToString("D"));
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() => fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task PersistsAndReconstructsTypedPerformanceObservations()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("system", WindowsPerformanceSystemObservationTool.ToolId),
            ("processes", WindowsPerformanceTopProcessesObservationTool.ToolId));
        var started = DateTimeOffset.UtcNow;
        var system = new WindowsPerformanceSystem(
            37.5,
            32_000_000,
            12_000_000,
            62,
            started,
            TimeSpan.FromMilliseconds(500));
        var processes = new WindowsPerformanceTopProcesses(
            started,
            TimeSpan.FromMilliseconds(500),
            [new WindowsPerformanceProcess(42, "worker.exe", 25, 8_000_000)],
            [new WindowsPerformanceProcess(42, "worker.exe", 25, 8_000_000)]);

        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution("system", WindowsPerformanceSystemObservationTool.ToolId, system));
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution("processes", WindowsPerformanceTopProcessesObservationTool.ToolId, processes));
        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Done."),
            started.AddSeconds(1)));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        Assert.NotNull(details);
        Assert.Collection(
            details.Investigation.StepExecutions,
            execution =>
            {
                var data = Assert.IsType<WindowsPerformanceSystem>(execution.Result!.Data);
                Assert.Equal(37.5, data.CpuUtilizationPercent);
                Assert.Equal(12_000_000UL, data.PhysicalMemoryAvailableBytes);
                Assert.Equal(TimeSpan.FromMilliseconds(500), data.SampleDuration);
            },
            execution =>
            {
                var data = Assert.IsType<WindowsPerformanceTopProcesses>(execution.Result!.Data);
                var process = Assert.Single(data.TopCpuProcesses);
                Assert.Equal(42, process.ProcessId);
                Assert.Equal("worker.exe", process.ProcessName);
                Assert.Equal(8_000_000UL, process.WorkingSetBytes);
                Assert.Equal(2, fixture.CountRows("WindowsPerformanceTopProcessEntries"));
            });
    }

    [Fact]
    public async Task FailedPerformanceObservationReconstructsWithoutAChildPayload()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("processes", WindowsPerformanceTopProcessesObservationTool.ToolId));
        var requestId = Guid.NewGuid();
        var execution = new InvestigationStepExecution(
            0,
            "processes",
            WindowsPerformanceTopProcessesObservationTool.ToolId,
            requestId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "1.0",
            InvestigationStepStatus.Failed,
            new ObservationResult(
                requestId,
                WindowsPerformanceTopProcessesObservationTool.ToolId,
                DateTimeOffset.UtcNow,
                ObservationStatus.Failed,
                Failure: new ObservationFailure("observation_failed", "The observation failed.")));

        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            InvestigationLifecycleStatus.Failed,
            new InvestigationOutcome(FailureCode: "investigation_failed", FailureMessage: "Failed."),
            DateTimeOffset.UtcNow));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        var reconstructed = Assert.Single(details!.Investigation.StepExecutions);
        Assert.Equal(ObservationStatus.Failed, reconstructed.Result!.Status);
        Assert.Null(reconstructed.Result.Data);
    }

    [Fact]
    public async Task RollsBackTypedChildRowsWhenExecutionFactWriteFails()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("processes", WindowsPerformanceTopProcessesObservationTool.ToolId));
        InstallPlanStepUpdateFailureTrigger(fixture);
        var data = new WindowsPerformanceTopProcesses(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(500),
            [new WindowsPerformanceProcess(7, "worker.exe", 10, 7_000)],
            [new WindowsPerformanceProcess(7, "worker.exe", 10, 7_000)]);

        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution("processes", WindowsPerformanceTopProcessesObservationTool.ToolId, data)));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("WindowsPerformanceTopProcessSnapshots"));
        Assert.Equal(0, fixture.CountRows("WindowsPerformanceTopProcessEntries"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
    }

    [Fact]
    public async Task RejectsInvalidTypedPerformanceValuesBeforeWritingRows()
    {
        using var fixture = await CreatePreparedFixtureAsync(
            ("system", WindowsPerformanceSystemObservationTool.ToolId));
        var invalid = new WindowsPerformanceSystem(
            double.NaN,
            ulong.MaxValue,
            0,
            50,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution("system", WindowsPerformanceSystemObservationTool.ToolId, invalid)));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("WindowsPerformanceSystemObservations"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MigratesRealLegacyFixtureToSchemaV5(int legacyVersion)
    {
        using var fixture = await CreateLegacyFixtureAsync(legacyVersion);

        fixture.Store.Initialize();

        Assert.Equal(5, fixture.GetSchemaVersion());
        Assert.True(fixture.HasTable("WindowsPerformanceSystemObservations"));
        Assert.True(fixture.HasTable("WindowsPerformanceTopProcessSnapshots"));
        Assert.True(fixture.HasTable("InvestigationReportStatements"));
        Assert.True(fixture.HasTable("InvestigationReportEvidenceReferences"));
        Assert.True(fixture.HasTable("WindowsRecentErrorEventSnapshots"));
        Assert.True(fixture.HasTable("WindowsRecentErrorEventEntries"));
        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        var execution = Assert.Single(details!.Investigation.StepExecutions);
        var data = Assert.IsType<WindowsSystemInfo>(execution.Result!.Data);
        Assert.Equal("Windows", data.Platform);
        Assert.Equal(26100, data.Build);
        Assert.Equal(1, fixture.CountRows("Observations"));
    }

    private static InvestigationStepExecution CreateExecution(
        string stepId,
        string toolId,
        IObservationData data)
    {
        var requestId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow;
        var observedAt = DateTimeOffset.UtcNow;
        return new(
            0,
            stepId,
            toolId,
            requestId,
            requestedAt,
            observedAt,
            "1.0",
            InvestigationStepStatus.Completed,
            new ObservationResult(
                requestId,
                toolId,
                observedAt,
                ObservationStatus.Succeeded,
            data));
    }

    private static WindowsRecentErrorEvents CreateRecentErrorEvents(bool isTruncated = false)
    {
        var windowEnd = DateTimeOffset.UtcNow;
        return new WindowsRecentErrorEvents(
            windowEnd - WindowsRecentErrorEventsValidation.ObservationWindow,
            windowEnd,
            isTruncated,
            [
                new WindowsDiagnosticEvent(
                    windowEnd.AddMinutes(-1),
                    WindowsEventChannelKind.Application,
                    "Application.Error",
                    1000,
                    WindowsEventSeverity.Critical),
                new WindowsDiagnosticEvent(
                    windowEnd.AddMinutes(-2),
                    WindowsEventChannelKind.System,
                    "System.Error",
                    41,
                    WindowsEventSeverity.Error)
            ]);
    }

    private static async Task<PerformanceFixture> CreateEventFixtureAsync()
    {
        var fixture = await CreatePreparedFixtureAsync(
            ("events", WindowsRecentErrorEventsObservationTool.ToolId));
        try
        {
            await fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(
                    "events",
                    WindowsRecentErrorEventsObservationTool.ToolId,
                    CreateRecentErrorEvents()));
            Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
                fixture.InvestigationId,
                InvestigationLifecycleStatus.Completed,
                new InvestigationOutcome(FinalAnswer: "Done."),
                DateTimeOffset.UtcNow));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static async Task<PerformanceFixture> CreatePreparedFixtureAsync(
        params (string StepId, string ToolId)[] steps)
    {
        var fixture = new PerformanceFixture();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await fixture.Store.CreateAsync(new Investigation(
                fixture.InvestigationId,
                "Question",
                "Question",
                now,
                null,
                null,
                InvestigationLifecycleStatus.Created,
                null,
                [],
                []));
            await fixture.Store.MarkRunningAsync(fixture.InvestigationId, now.AddSeconds(1));
            await fixture.Store.AppendPlanAsync(
                fixture.InvestigationId,
                new InvestigationPlanHistoryEntry(
                    0,
                    now.AddSeconds(2),
                    new InvestigationPlan(
                        "Question",
                        steps.Select(step => new InvestigationStep(step.StepId, step.ToolId)).ToArray())));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static void InstallPlanStepUpdateFailureTrigger(PerformanceFixture fixture)
    {
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER FailPlanStepUpdate
            BEFORE UPDATE OF Status ON PlanSteps
            BEGIN
                SELECT RAISE(ABORT, 'forced plan step update failure');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<LegacyFixture> CreateLegacyFixtureAsync(int version)
    {
        var fixture = new LegacyFixture();
        try
        {
            fixture.CreateSchema(version);
            fixture.SeedWindowsSystemInfo();
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private sealed class PerformanceFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "AegisTests",
            Guid.NewGuid().ToString("N"));

        public PerformanceFixture()
        {
            Database = new SqliteDatabase(Path.Combine(_directory, "history.db"));
            Store = new SqliteInvestigationStore(Database);
            Store.Initialize();
            InvestigationId = Guid.NewGuid();
        }

        public SqliteDatabase Database { get; }
        public SqliteInvestigationStore Store { get; }
        public Guid InvestigationId { get; }

        public long CountRows(string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class LegacyFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "AegisTests",
            Guid.NewGuid().ToString("N"));

        public LegacyFixture()
        {
            Database = new SqliteDatabase(Path.Combine(_directory, "history.db"));
            Store = new SqliteInvestigationStore(Database);
            InvestigationId = Guid.NewGuid();
        }

        public SqliteDatabase Database { get; }
        public SqliteInvestigationStore Store { get; }
        public Guid InvestigationId { get; }

        public void CreateSchema(int version)
        {
            Directory.CreateDirectory(_directory);
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            Execute(connection, """
                PRAGMA foreign_keys = ON;
                CREATE TABLE SchemaVersions (Version INTEGER NOT NULL);
                CREATE TABLE Investigations (
                    InvestigationId TEXT PRIMARY KEY NOT NULL,
                    Question TEXT NOT NULL,
                    Objective TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    StartedAtUtc TEXT NULL,
                    CompletedAtUtc TEXT NULL,
                    LifecycleStatus INTEGER NOT NULL,
                    FinalAnswer TEXT NULL,
                    FailureCode TEXT NULL,
                    FailureMessage TEXT NULL
                );
                CREATE TABLE Plans (
                    InvestigationId TEXT NOT NULL,
                    PlanSequence INTEGER NOT NULL,
                    AcceptedAtUtc TEXT NOT NULL,
                    Objective TEXT NOT NULL,
                    PRIMARY KEY (InvestigationId, PlanSequence),
                    FOREIGN KEY (InvestigationId) REFERENCES Investigations(InvestigationId)
                );
                CREATE TABLE PlanSteps (
                    InvestigationId TEXT NOT NULL,
                    PlanSequence INTEGER NOT NULL,
                    StepId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL,
                    ToolId TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    PRIMARY KEY (InvestigationId, PlanSequence, StepId),
                    FOREIGN KEY (InvestigationId, PlanSequence)
                        REFERENCES Plans(InvestigationId, PlanSequence)
                );
                CREATE TABLE StepExecutions (
                    ExecutionId INTEGER PRIMARY KEY AUTOINCREMENT,
                    InvestigationId TEXT NOT NULL,
                    PlanSequence INTEGER NOT NULL,
                    StepId TEXT NOT NULL,
                    ToolId TEXT NOT NULL,
                    RequestId TEXT NOT NULL,
                    RequestedAtUtc TEXT NOT NULL,
                    ObservedAtUtc TEXT NULL,
                    ToolContractVersion TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    ObservationId INTEGER NULL,
                    UNIQUE (InvestigationId, PlanSequence, StepId),
                    FOREIGN KEY (InvestigationId, PlanSequence, StepId)
                        REFERENCES PlanSteps(InvestigationId, PlanSequence, StepId),
                    FOREIGN KEY (ObservationId) REFERENCES Observations(ObservationId)
                );
                CREATE TABLE Observations (
                    ObservationId INTEGER PRIMARY KEY AUTOINCREMENT,
                    InvestigationId TEXT NOT NULL,
                    PlanSequence INTEGER NOT NULL,
                    StepId TEXT NOT NULL,
                    RequestId TEXT NOT NULL,
                    ToolId TEXT NOT NULL,
                    ObservedAtUtc TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    FailureCode TEXT NULL,
                    FailureMessage TEXT NULL,
                    Platform TEXT NULL,
                    OsVersion TEXT NULL,
                    Build INTEGER NULL,
                    Architecture TEXT NULL,
                    FOREIGN KEY (InvestigationId, PlanSequence, StepId)
                        REFERENCES PlanSteps(InvestigationId, PlanSequence, StepId)
                );
                CREATE TABLE Baselines (
                    BaselineId TEXT PRIMARY KEY NOT NULL,
                    SourceInvestigationId TEXT NOT NULL,
                    SourceStepId TEXT NOT NULL,
                    ToolId TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    RequestId TEXT NOT NULL,
                    ObservedAtUtc TEXT NOT NULL,
                    Platform TEXT NOT NULL,
                    OsVersion TEXT NOT NULL,
                    Build INTEGER NULL,
                    Architecture TEXT NOT NULL,
                    FOREIGN KEY (SourceInvestigationId) REFERENCES Investigations(InvestigationId)
                );
                CREATE INDEX IX_Investigations_CreatedAtUtc
                    ON Investigations(CreatedAtUtc DESC);
                CREATE INDEX IX_StepExecutions_Investigation
                    ON StepExecutions(InvestigationId, PlanSequence, ExecutionId);
                """);
            Execute(connection, "INSERT INTO SchemaVersions (Version) VALUES (1);");
            if (version >= 2)
            {
                Execute(connection, """
                    DROP INDEX IF EXISTS IX_StepExecutions_Investigation;
                    ALTER TABLE StepExecutions RENAME TO StepExecutions_v1;
                    CREATE TABLE StepExecutions (
                        ExecutionId INTEGER PRIMARY KEY AUTOINCREMENT,
                        InvestigationId TEXT NOT NULL,
                        PlanSequence INTEGER NOT NULL,
                        StepId TEXT NOT NULL,
                        ToolId TEXT NOT NULL,
                        RequestId TEXT NOT NULL,
                        RequestedAtUtc TEXT NOT NULL,
                        ObservedAtUtc TEXT NULL,
                        ToolContractVersion TEXT NOT NULL,
                        Status INTEGER NOT NULL,
                        ObservationId INTEGER NULL,
                        UNIQUE (InvestigationId, PlanSequence, StepId),
                        FOREIGN KEY (InvestigationId, PlanSequence, StepId)
                            REFERENCES PlanSteps(InvestigationId, PlanSequence, StepId),
                        FOREIGN KEY (ObservationId) REFERENCES Observations(ObservationId)
                    );
                    INSERT INTO StepExecutions
                        (ExecutionId, InvestigationId, PlanSequence, StepId, ToolId, RequestId,
                         RequestedAtUtc, ObservedAtUtc, ToolContractVersion, Status, ObservationId)
                    SELECT ExecutionId, InvestigationId, PlanSequence, StepId, ToolId, RequestId,
                           RequestedAtUtc, ObservedAtUtc, ToolContractVersion, Status, ObservationId
                    FROM StepExecutions_v1;
                    DROP TABLE StepExecutions_v1;
                    CREATE INDEX IX_StepExecutions_Investigation
                        ON StepExecutions(InvestigationId, PlanSequence, ExecutionId);
                    INSERT INTO SchemaVersions (Version) VALUES (2);
                    """);
            }

            if (version >= 3)
            {
                Execute(connection, """
                    CREATE TABLE WindowsPerformanceSystemObservations (
                        ObservationId INTEGER PRIMARY KEY NOT NULL,
                        SampleStartedAtUtc TEXT NOT NULL,
                        SampleDurationTicks INTEGER NOT NULL,
                        CpuUtilizationPercent REAL NOT NULL,
                        PhysicalMemoryTotalBytes INTEGER NOT NULL,
                        PhysicalMemoryAvailableBytes INTEGER NOT NULL,
                        MemoryLoadPercent INTEGER NOT NULL,
                        CHECK (SampleDurationTicks > 0),
                        CHECK (CpuUtilizationPercent >= 0 AND CpuUtilizationPercent <= 100),
                        CHECK (PhysicalMemoryTotalBytes >= 0),
                        CHECK (PhysicalMemoryAvailableBytes >= 0),
                        CHECK (PhysicalMemoryAvailableBytes <= PhysicalMemoryTotalBytes),
                        CHECK (MemoryLoadPercent >= 0 AND MemoryLoadPercent <= 100),
                        FOREIGN KEY (ObservationId) REFERENCES Observations(ObservationId)
                    );
                    CREATE TABLE WindowsPerformanceTopProcessSnapshots (
                        ObservationId INTEGER PRIMARY KEY NOT NULL,
                        SampleStartedAtUtc TEXT NOT NULL,
                        SampleDurationTicks INTEGER NOT NULL,
                        CHECK (SampleDurationTicks > 0),
                        FOREIGN KEY (ObservationId) REFERENCES Observations(ObservationId)
                    );
                    CREATE TABLE WindowsPerformanceTopProcessEntries (
                        ObservationId INTEGER NOT NULL,
                        RankingKind TEXT NOT NULL,
                        RankingOrdinal INTEGER NOT NULL,
                        ProcessId INTEGER NOT NULL,
                        ProcessName TEXT NOT NULL,
                        CpuUtilizationPercent REAL NOT NULL,
                        WorkingSetBytes INTEGER NOT NULL,
                        CHECK (RankingKind IN ('cpu', 'memory')),
                        CHECK (RankingOrdinal >= 0 AND RankingOrdinal < 10),
                        CHECK (ProcessId > 0),
                        CHECK (length(ProcessName) > 0),
                        CHECK (CpuUtilizationPercent >= 0 AND CpuUtilizationPercent <= 100),
                        CHECK (WorkingSetBytes >= 0),
                        PRIMARY KEY (ObservationId, RankingKind, RankingOrdinal),
                        FOREIGN KEY (ObservationId) REFERENCES WindowsPerformanceTopProcessSnapshots(ObservationId)
                    );
                    CREATE INDEX IX_WindowsPerformanceTopProcessEntries_Observation
                        ON WindowsPerformanceTopProcessEntries(ObservationId, RankingKind, RankingOrdinal);
                    INSERT INTO SchemaVersions (Version) VALUES (3);
                    """);
            }

            if (version >= 4)
            {
                Execute(connection, """
                    CREATE TABLE InvestigationReportStatements (
                        InvestigationId TEXT NOT NULL,
                        SectionKind TEXT NOT NULL,
                        StatementOrdinal INTEGER NOT NULL,
                        Text TEXT NOT NULL,
                        PRIMARY KEY (InvestigationId, SectionKind, StatementOrdinal),
                        CHECK (SectionKind IN ('ObservedFacts', 'Conclusions', 'Hypotheses', 'Uncertainties', 'Recommendations')),
                        CHECK (StatementOrdinal >= 0 AND StatementOrdinal < 8),
                        CHECK (length(Text) > 0),
                        FOREIGN KEY (InvestigationId) REFERENCES Investigations(InvestigationId)
                    );
                    CREATE TABLE InvestigationReportEvidenceReferences (
                        InvestigationId TEXT NOT NULL,
                        SectionKind TEXT NOT NULL,
                        StatementOrdinal INTEGER NOT NULL,
                        ReferenceOrdinal INTEGER NOT NULL,
                        StepId TEXT NOT NULL,
                        PRIMARY KEY (InvestigationId, SectionKind, StatementOrdinal, ReferenceOrdinal),
                        CHECK (SectionKind IN ('ObservedFacts', 'Conclusions', 'Hypotheses')),
                        CHECK (StatementOrdinal >= 0 AND StatementOrdinal < 8),
                        CHECK (ReferenceOrdinal >= 0 AND ReferenceOrdinal < 4),
                        CHECK (length(StepId) > 0),
                        FOREIGN KEY (InvestigationId, SectionKind, StatementOrdinal)
                            REFERENCES InvestigationReportStatements(InvestigationId, SectionKind, StatementOrdinal)
                    );
                    CREATE INDEX IX_InvestigationReportStatements_Investigation
                        ON InvestigationReportStatements(InvestigationId, SectionKind, StatementOrdinal);
                    CREATE INDEX IX_InvestigationReportEvidenceReferences_Investigation
                        ON InvestigationReportEvidenceReferences(InvestigationId, SectionKind, StatementOrdinal, ReferenceOrdinal);
                    INSERT INTO SchemaVersions (Version) VALUES (4);
                    """);
            }
        }

        public void SeedWindowsSystemInfo()
        {
            var now = DateTimeOffset.UtcNow;
            var investigation = InvestigationId.ToString("D");
            var request = Guid.NewGuid().ToString("D");
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            Execute(connection, """
                INSERT INTO Investigations
                    (InvestigationId, Question, Objective, CreatedAtUtc, StartedAtUtc,
                     CompletedAtUtc, LifecycleStatus, FinalAnswer)
                VALUES ($investigation, 'Question', 'Question', $created, $started,
                        $completed, 2, 'Done.');
                INSERT INTO Plans
                    (InvestigationId, PlanSequence, AcceptedAtUtc, Objective)
                VALUES ($investigation, 0, $accepted, 'Question');
                INSERT INTO PlanSteps
                    (InvestigationId, PlanSequence, StepId, Ordinal, ToolId, Status)
                VALUES ($investigation, 0, 'step-1', 0, $tool, 1);
                INSERT INTO Observations
                    (InvestigationId, PlanSequence, StepId, RequestId, ToolId, ObservedAtUtc,
                     Status, Platform, OsVersion, Build, Architecture)
                VALUES ($investigation, 0, 'step-1', $request, $tool, $observed,
                        0, 'Windows', '10.0', 26100, 'X64');
                INSERT INTO StepExecutions
                    (InvestigationId, PlanSequence, StepId, ToolId, RequestId,
                     RequestedAtUtc, ObservedAtUtc, ToolContractVersion, Status, ObservationId)
                VALUES ($investigation, 0, 'step-1', $tool, $request,
                        $requested, $observed, '1.0', 1, last_insert_rowid());
                """,
                ("$investigation", investigation),
                ("$tool", WindowsSystemInfoObservationTool.ToolId),
                ("$request", request),
                ("$created", FormatDate(now)),
                ("$started", FormatDate(now.AddSeconds(1))),
                ("$completed", FormatDate(now.AddSeconds(2))),
                ("$accepted", FormatDate(now.AddSeconds(1))),
                ("$observed", FormatDate(now.AddSeconds(2))),
                ("$requested", FormatDate(now.AddSeconds(1))));
        }

        public int GetSchemaVersion()
        {
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(Version) FROM SchemaVersions;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public bool HasTable(string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        public long CountRows(string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static void Execute(
            SqliteConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            command.ExecuteNonQuery();
        }

        private static string FormatDate(DateTimeOffset value) => value.ToString("O");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
