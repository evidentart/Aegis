using Aegis.Core;
using Aegis.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aegis.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SQLiteInitializationIsIdempotentAndReconstructsTypedHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AegisTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "history.db");

        try
        {
            var database = new SqliteDatabase(databasePath);
            var store = new SqliteInvestigationStore(database);
            store.Initialize();
            store.Initialize();

            var investigationId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            await store.CreateAsync(new Investigation(
                investigationId,
                "Question",
                "Question",
                createdAt,
                null,
                null,
                InvestigationLifecycleStatus.Created,
                null,
                [],
                []));
            await store.MarkRunningAsync(investigationId, createdAt.AddSeconds(1));

            var plan = new InvestigationPlan(
                "Question",
                [new InvestigationStep("step-1", WindowsSystemInfoObservationTool.ToolId)]);
            await store.AppendPlanAsync(
                investigationId,
                new InvestigationPlanHistoryEntry(0, createdAt.AddSeconds(2), plan));

            var observation = new ObservationResult(
                Guid.NewGuid(),
                WindowsSystemInfoObservationTool.ToolId,
                createdAt.AddSeconds(3),
                ObservationStatus.Succeeded,
                new WindowsSystemInfo("Windows", "10.0", 26100, "X64"));
            await store.AppendStepExecutionAsync(
                investigationId,
                new InvestigationStepExecution(
                    0,
                    "step-1",
                    WindowsSystemInfoObservationTool.ToolId,
                    observation.RequestId,
                    createdAt.AddSeconds(2),
                    observation.ObservedAtUtc,
                    "1",
                    InvestigationStepStatus.Completed,
                    observation));

            Assert.True(await store.CommitTerminalOutcomeAsync(
                investigationId,
                InvestigationLifecycleStatus.Completed,
                new InvestigationOutcome(FinalAnswer: "Done."),
                createdAt.AddSeconds(4)));
            Assert.False(await store.CommitTerminalOutcomeAsync(
                investigationId,
                InvestigationLifecycleStatus.Completed,
                new InvestigationOutcome(FinalAnswer: "Duplicate."),
                createdAt.AddSeconds(5)));

            var details = await store.GetAsync(investigationId);
            Assert.NotNull(details);
            Assert.Equal(InvestigationLifecycleStatus.Completed, details.Investigation.LifecycleStatus);
            Assert.Equal("Done.", details.Investigation.Outcome?.FinalAnswer);
            Assert.Null(details.Investigation.Outcome?.FailureCode);
            Assert.Null(details.Investigation.Outcome?.FailureMessage);
            Assert.Single(details.Investigation.Plans);
            var execution = Assert.Single(details.Investigation.StepExecutions);
            var data = Assert.IsType<WindowsSystemInfo>(execution.Result!.Data);
            Assert.Equal(26100, data.Build);
            Assert.Equal("X64", data.Architecture);

            await store.CreateAsync(new Baseline(
                Guid.NewGuid(),
                investigationId,
                "step-1",
                WindowsSystemInfoObservationTool.ToolId,
                createdAt.AddSeconds(5),
                observation));
            var baseline = Assert.Single(await store.ListBaselinesAsync());
            Assert.Equal(investigationId, baseline.SourceInvestigationId);
            Assert.Equal(WindowsSystemInfoObservationTool.ToolId, baseline.ToolId);
            Assert.Equal("Windows", baseline.Platform);
            Assert.Equal("10.0", baseline.OsVersion);
            Assert.Equal(26100, baseline.Build);
            Assert.Equal("X64", baseline.Architecture);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SQLiteReconstructsReplannedPlansAndExecutionsInOrder()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var firstExecution = CreateExecution(fixture, stepId: "step-1");
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, firstExecution);

        await fixture.Store.AppendPlanAsync(
            fixture.InvestigationId,
            new InvestigationPlanHistoryEntry(
                1,
                DateTimeOffset.UtcNow,
                new InvestigationPlan(
                    "Question",
                    [new InvestigationStep("step-2", WindowsSystemInfoObservationTool.ToolId)])));
        var secondExecution = CreateExecution(
            fixture,
            planSequence: 1,
            stepId: "step-2");
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, secondExecution);
        await CommitCompletedAsync(fixture);

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        Assert.NotNull(details);
        Assert.Equal(InvestigationLifecycleStatus.Completed, details.Investigation.LifecycleStatus);
        Assert.Collection(
            details.Investigation.Plans,
            firstPlan =>
            {
                Assert.Equal(0, firstPlan.PlanSequence);
                Assert.Equal("Question", firstPlan.Plan.Objective);
                Assert.Equal(InvestigationStepStatus.Completed, Assert.Single(firstPlan.Plan.Steps).Status);
            },
            secondPlan =>
            {
                Assert.Equal(1, secondPlan.PlanSequence);
                Assert.Equal("Question", secondPlan.Plan.Objective);
                Assert.Equal(InvestigationStepStatus.Completed, Assert.Single(secondPlan.Plan.Steps).Status);
            });
        Assert.Collection(
            details.Investigation.StepExecutions,
            execution =>
            {
                Assert.Equal(0, execution.PlanSequence);
                Assert.Equal("step-1", execution.StepId);
                Assert.Equal(ObservationStatus.Succeeded, execution.Result?.Status);
            },
            execution =>
            {
                Assert.Equal(1, execution.PlanSequence);
                Assert.Equal("step-2", execution.StepId);
                Assert.Equal(ObservationStatus.Succeeded, execution.Result?.Status);
            });
    }

    [Fact]
    public async Task SQLiteReconstructsCancelledExecutionAndSkippedSteps()
    {
        using var fixture = await CreatePreparedFixtureAsync("step-1", "step-2");
        var cancelledExecution = CreateExecution(
            fixture,
            stepId: "step-1",
            status: InvestigationStepStatus.Cancelled,
            includeResult: false);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, cancelledExecution);
        await fixture.Store.MarkStepsSkippedAsync(
            fixture.InvestigationId,
            planSequence: 0,
            ["step-2"]);
        await CommitTerminalAsync(
            fixture,
            InvestigationLifecycleStatus.Cancelled,
            new InvestigationOutcome(
                FailureCode: "cancelled",
                FailureMessage: "The investigation was cancelled."));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        Assert.NotNull(details);
        Assert.Equal(InvestigationLifecycleStatus.Cancelled, details.Investigation.LifecycleStatus);
        Assert.Equal("cancelled", details.Investigation.Outcome?.FailureCode);
        Assert.Equal("The investigation was cancelled.", details.Investigation.Outcome?.FailureMessage);
        var steps = Assert.Single(details.Investigation.Plans).Plan.Steps;
        Assert.Equal(InvestigationStepStatus.Cancelled, steps[0].Status);
        Assert.Equal(InvestigationStepStatus.Skipped, steps[1].Status);
        var execution = Assert.Single(details.Investigation.StepExecutions);
        Assert.Equal(InvestigationStepStatus.Cancelled, execution.Status);
        Assert.Null(execution.Result);
    }

    [Fact]
    public async Task SQLiteReconstructsFailedExecutionAndTerminalOutcome()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var failedExecution = CreateExecution(
            fixture,
            status: InvestigationStepStatus.Failed,
            resultStatus: ObservationStatus.Failed);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, failedExecution);
        await CommitTerminalAsync(
            fixture,
            InvestigationLifecycleStatus.Failed,
            new InvestigationOutcome(
                FailureCode: "investigation_failed",
                FailureMessage: "The investigation could not be completed."));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);

        Assert.NotNull(details);
        Assert.Equal(InvestigationLifecycleStatus.Failed, details.Investigation.LifecycleStatus);
        Assert.Equal("investigation_failed", details.Investigation.Outcome?.FailureCode);
        Assert.Equal(
            "The investigation could not be completed.",
            details.Investigation.Outcome?.FailureMessage);
        Assert.Equal(
            InvestigationStepStatus.Failed,
            Assert.Single(Assert.Single(details.Investigation.Plans).Plan.Steps).Status);
        var execution = Assert.Single(details.Investigation.StepExecutions);
        Assert.Equal(InvestigationStepStatus.Failed, execution.Status);
        Assert.Equal(ObservationStatus.Failed, execution.Result?.Status);
        Assert.Equal("observation_failed", execution.Result?.Failure?.Code);
    }

    [Fact]
    public async Task RejectsNonTerminalStatusWithoutChangingInvestigation()
    {
        using var fixture = await CreatePreparedFixtureAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.CommitTerminalOutcomeAsync(
                fixture.InvestigationId,
                InvestigationLifecycleStatus.Running,
                new InvestigationOutcome(FinalAnswer: "Not terminal."),
                DateTimeOffset.UtcNow));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);
        Assert.NotNull(details);
        Assert.Equal(InvestigationLifecycleStatus.Running, details.Investigation.LifecycleStatus);
        Assert.Null(details.Investigation.CompletedAtUtc);
        Assert.Null(details.Investigation.Outcome);
    }

    [Fact]
    public async Task RejectsMismatchedExecutionAndResultIdentityWithoutPartialRows()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var requestId = Guid.NewGuid();
        var execution = CreateExecution(
            fixture,
            requestId,
            resultRequestId: Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
    }

    [Fact]
    public async Task RejectsDuplicateExecutionWithoutAddingPartialRows()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var first = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, first);
        var observationsBefore = fixture.CountRows("Observations");
        var executionsBefore = fixture.CountRows("StepExecutions");

        var duplicate = CreateExecution(fixture);
        await Assert.ThrowsAsync<InvestigationPersistenceException>(() =>
            fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, duplicate));

        Assert.Equal(observationsBefore, fixture.CountRows("Observations"));
        Assert.Equal(executionsBefore, fixture.CountRows("StepExecutions"));
    }

    [Fact]
    public async Task RejectsExecutionForNonPendingPlanStepWithoutOverwritingStatus()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await fixture.Store.MarkStepsSkippedAsync(
            fixture.InvestigationId,
            planSequence: 0,
            ["step-1"]);

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(fixture)));

        var details = await fixture.Store.GetAsync(fixture.InvestigationId);
        Assert.Equal(
            InvestigationStepStatus.Skipped,
            Assert.Single(Assert.Single(details!.Investigation.Plans).Plan.Steps).Status);
        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
    }

    [Fact]
    public async Task EnforcesObservationForeignKeyOnStepExecutions()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO StepExecutions
                (InvestigationId, PlanSequence, StepId, ToolId, RequestId,
                 RequestedAtUtc, ToolContractVersion, Status, ObservationId)
            VALUES ($investigation, 0, 'step-1', $tool, $request,
                    $requested, '1', $status, 999999);
            """;
        command.Parameters.AddWithValue("$investigation", fixture.InvestigationId.ToString("D"));
        command.Parameters.AddWithValue("$tool", WindowsSystemInfoObservationTool.ToolId);
        command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$requested", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", (int)InvestigationStepStatus.Completed);

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task RollsBackObservationAndExecutionWhenPlanStepUpdateFails()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        InstallPlanStepUpdateFailureTrigger(fixture);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(fixture)));

        Assert.Contains("forced plan step update failure", exception.Message);
        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        Assert.Equal(InvestigationStepStatus.Pending, await GetStepStatusAsync(fixture));
    }

    [Fact]
    public async Task RejectsCompletedExecutionWithoutResult()
    {
        using var fixture = await CreatePreparedFixtureAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(fixture, includeResult: false)));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        Assert.Equal(InvestigationStepStatus.Pending, await GetStepStatusAsync(fixture));
    }

    [Fact]
    public async Task RejectsExecutionWhenResultStatusDoesNotMatchExecutionStatus()
    {
        using var fixture = await CreatePreparedFixtureAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(
                    fixture,
                    status: InvestigationStepStatus.Failed,
                    resultStatus: ObservationStatus.Succeeded)));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        Assert.Equal(InvestigationStepStatus.Pending, await GetStepStatusAsync(fixture));
    }

    [Fact]
    public async Task RejectsExecutionWhenToolDoesNotMatchPersistedPlanStep()
    {
        using var fixture = await CreatePreparedFixtureAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(
                    fixture,
                    executionToolId: "other.tool",
                    resultToolId: "other.tool")));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        Assert.Equal(InvestigationStepStatus.Pending, await GetStepStatusAsync(fixture));
    }

    [Fact]
    public async Task RejectsBaselineWithInvalidSourceStep()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CreateAsync(CreateBaseline(fixture, "missing-step", execution.Result!)));

        Assert.Equal(0, fixture.CountRows("Baselines"));
    }

    [Fact]
    public async Task RejectsBaselineForNonCompletedSourceExecution()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(
            fixture,
            status: InvestigationStepStatus.Failed,
            resultStatus: ObservationStatus.Failed);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CreateAsync(CreateBaseline(fixture, "step-1", execution.Result!)));

        Assert.Equal(0, fixture.CountRows("Baselines"));
    }

    [Fact]
    public async Task RejectsBaselineWhileSourceInvestigationIsRunning()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CreateAsync(CreateBaseline(fixture, "step-1", execution.Result!)));

        Assert.Equal(0, fixture.CountRows("Baselines"));
    }

    [Fact]
    public async Task RejectsBaselineWhenSuppliedObservationDiffersFromPersistedObservation()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);

        var mismatchedObservation = execution.Result! with
        {
            Data = new WindowsSystemInfo("Windows", "10.0", 99999, "X64")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CreateAsync(CreateBaseline(fixture, "step-1", mismatchedObservation)));

        Assert.Equal(0, fixture.CountRows("Baselines"));
    }

    [Fact]
    public async Task BaselineServiceRejectsNonCompletedSourceExecution()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(
            fixture,
            status: InvestigationStepStatus.Failed,
            resultStatus: ObservationStatus.Failed);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);

        var service = new BaselineService(
            fixture.Store,
            fixture.Store,
            new ObservationRegistry([new WindowsSystemInfoObservationTool()]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateFromStepAsync(fixture.InvestigationId, "step-1"));

        Assert.Equal(0, fixture.CountRows("Baselines"));
    }

    [Fact]
    public async Task RejectsPostTerminalAppendPlan()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await CommitCompletedAsync(fixture);

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() =>
            fixture.Store.AppendPlanAsync(
                fixture.InvestigationId,
                new InvestigationPlanHistoryEntry(
                    1,
                    DateTimeOffset.UtcNow,
                    new InvestigationPlan(
                        "Question",
                        [new InvestigationStep("step-2", WindowsSystemInfoObservationTool.ToolId)]))));

        Assert.Equal(1, fixture.CountRows("Plans"));
        Assert.Equal(1, fixture.CountRows("PlanSteps"));
    }

    [Fact]
    public async Task RejectsPostTerminalAppendStepExecution()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await CommitCompletedAsync(fixture);

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() =>
            fixture.Store.AppendStepExecutionAsync(
                fixture.InvestigationId,
                CreateExecution(fixture)));

        Assert.Equal(0, fixture.CountRows("Observations"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        Assert.Equal(InvestigationStepStatus.Pending, await GetStepStatusAsync(fixture));
    }

    [Fact]
    public async Task RejectsPostTerminalMarkStepsSkipped()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await CommitCompletedAsync(fixture);

        await Assert.ThrowsAsync<InvestigationPersistenceException>(() =>
            fixture.Store.MarkStepsSkippedAsync(
                fixture.InvestigationId,
                planSequence: 0,
                ["step-1"]));

        Assert.Equal(InvestigationStepStatus.Pending, await GetStepStatusAsync(fixture));
    }

    private static InvestigationStepExecution CreateExecution(
        DatabaseFixture fixture,
        Guid? requestId = null,
        Guid? resultRequestId = null,
        string? executionToolId = null,
        string? resultToolId = null,
        int planSequence = 0,
        string stepId = "step-1",
        InvestigationStepStatus status = InvestigationStepStatus.Completed,
        ObservationStatus resultStatus = ObservationStatus.Succeeded,
        bool includeResult = true)
    {
        var request = requestId ?? Guid.NewGuid();
        var executionTool = executionToolId ?? WindowsSystemInfoObservationTool.ToolId;
        var observation = includeResult
            ? new ObservationResult(
                resultRequestId ?? request,
                resultToolId ?? executionTool,
                DateTimeOffset.UtcNow,
                resultStatus,
                resultStatus == ObservationStatus.Succeeded
                    ? new WindowsSystemInfo("Windows", "10.0", 26100, "X64")
                    : null,
                resultStatus == ObservationStatus.Failed
                    ? new ObservationFailure("observation_failed", "The observation failed.")
                    : null)
            : null;
        return new InvestigationStepExecution(
            planSequence,
            stepId,
            executionTool,
            request,
            DateTimeOffset.UtcNow,
            observation?.ObservedAtUtc,
            "1",
            status,
            observation);
    }

    private static void InstallPlanStepUpdateFailureTrigger(DatabaseFixture fixture)
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

    private static Baseline CreateBaseline(
        DatabaseFixture fixture,
        string sourceStepId,
        ObservationResult observation) =>
        new(
            Guid.NewGuid(),
            fixture.InvestigationId,
            sourceStepId,
            WindowsSystemInfoObservationTool.ToolId,
            DateTimeOffset.UtcNow,
            observation);

    private static Task CommitCompletedAsync(DatabaseFixture fixture) =>
        CommitTerminalAsync(
            fixture,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Done."));

    private static async Task CommitTerminalAsync(
        DatabaseFixture fixture,
        InvestigationLifecycleStatus status,
        InvestigationOutcome outcome)
    {
        Assert.True(await fixture.Store.CommitTerminalOutcomeAsync(
            fixture.InvestigationId,
            status,
            outcome,
            DateTimeOffset.UtcNow));
    }

    private static async Task<InvestigationStepStatus> GetStepStatusAsync(DatabaseFixture fixture)
    {
        var details = await fixture.Store.GetAsync(fixture.InvestigationId);
        return Assert.Single(Assert.Single(details!.Investigation.Plans).Plan.Steps).Status;
    }

    private static async Task<DatabaseFixture> CreatePreparedFixtureAsync(params string[] stepIds)
    {
        var fixture = new DatabaseFixture();
        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var preparedStepIds = stepIds.Length == 0 ? ["step-1"] : stepIds;
            await fixture.Store.CreateAsync(new Investigation(
                fixture.InvestigationId,
                "Question",
                "Question",
                createdAt,
                null,
                null,
                InvestigationLifecycleStatus.Created,
                null,
                [],
                []));
            await fixture.Store.MarkRunningAsync(fixture.InvestigationId, createdAt.AddSeconds(1));
            await fixture.Store.AppendPlanAsync(
                fixture.InvestigationId,
                new InvestigationPlanHistoryEntry(
                    0,
                    createdAt.AddSeconds(2),
                    new InvestigationPlan(
                        "Question",
                        preparedStepIds
                            .Select(stepId => new InvestigationStep(
                                stepId,
                                WindowsSystemInfoObservationTool.ToolId))
                            .ToArray())));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "AegisTests",
                Guid.NewGuid().ToString("N"));
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
}
