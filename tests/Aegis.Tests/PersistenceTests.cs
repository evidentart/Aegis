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

    [Fact]
    public async Task DeletesCompletedInvestigationAndAllCoreHistoryRows()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(fixture));
        await CommitCompletedAsync(fixture);

        var result = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(InvestigationDeletionStatus.Deleted, result.Status);
        Assert.Null(await fixture.Store.GetAsync(fixture.InvestigationId));
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.Equal(0, fixture.CountRows("Investigations"));
        Assert.Equal(0, fixture.CountRows("Plans"));
        Assert.Equal(0, fixture.CountRows("PlanSteps"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        Assert.Equal(0, fixture.CountRows("Observations"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Theory]
    [InlineData(InvestigationLifecycleStatus.Failed)]
    [InlineData(InvestigationLifecycleStatus.Cancelled)]
    public async Task DeletesFailedAndCancelledInvestigations(
        InvestigationLifecycleStatus terminalStatus)
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await CommitTerminalAsync(
            fixture,
            terminalStatus,
            new InvestigationOutcome(FailureCode: terminalStatus.ToString()));

        var result = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(InvestigationDeletionStatus.Deleted, result.Status);
        Assert.Null(await fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task RejectsCreatedInvestigationWithoutChangingIt()
    {
        using var fixture = new DatabaseFixture();
        await CreateInvestigationAsync(fixture.Store, fixture.InvestigationId);

        var result = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(InvestigationDeletionStatus.NotTerminal, result.Status);
        Assert.NotNull(await fixture.Store.GetAsync(fixture.InvestigationId));
        Assert.Equal(1, fixture.CountRows("Investigations"));
    }

    [Fact]
    public async Task RejectsRunningInvestigationWithoutChangingIt()
    {
        using var fixture = await CreatePreparedFixtureAsync();

        var result = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(InvestigationDeletionStatus.NotTerminal, result.Status);
        Assert.NotNull(await fixture.Store.GetAsync(fixture.InvestigationId));
        Assert.Equal(1, fixture.CountRows("Plans"));
        Assert.Equal(1, fixture.CountRows("PlanSteps"));
    }

    [Fact]
    public async Task ReturnsNotFoundForUnknownAndAlreadyDeletedInvestigations()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var unknownResult = await fixture.Store.DeleteInvestigationAsync(Guid.NewGuid());

        Assert.Equal(InvestigationDeletionStatus.NotFound, unknownResult.Status);
        await CommitCompletedAsync(fixture);
        var firstDelete = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);
        var secondDelete = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(InvestigationDeletionStatus.Deleted, firstDelete.Status);
        Assert.Equal(InvestigationDeletionStatus.NotFound, secondDelete.Status);
    }

    [Fact]
    public async Task PreservesBaselineAndSourceInvestigationWhenDeletingIsBlocked()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);
        await fixture.Store.CreateAsync(CreateBaseline(fixture, "step-1", execution.Result!));

        var result = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(InvestigationDeletionStatus.BaselineProtected, result.Status);
        Assert.NotNull(await fixture.Store.GetAsync(fixture.InvestigationId));
        var baseline = Assert.Single(await fixture.Store.ListBaselinesAsync());
        Assert.Equal(fixture.InvestigationId, baseline.SourceInvestigationId);
        Assert.Equal(1, fixture.CountRows("Baselines"));
    }

    [Fact]
    public async Task ClearHistoryDeletesEligibleRowsAndPreservesProtectedAndNonTerminalRows()
    {
        using var fixture = new DatabaseFixture();
        var eligibleId = Guid.NewGuid();
        var protectedId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var createdId = Guid.NewGuid();

        await PrepareInvestigationAsync(fixture.Store, eligibleId);
        await CommitTerminalAsync(
            fixture.Store,
            eligibleId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Eligible."));

        await PrepareInvestigationAsync(fixture.Store, protectedId);
        var protectedExecution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(protectedId, protectedExecution);
        await CommitTerminalAsync(
            fixture.Store,
            protectedId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Protected."));
        await fixture.Store.CreateAsync(
            CreateBaseline(protectedId, "step-1", protectedExecution.Result!));

        await PrepareInvestigationAsync(fixture.Store, runningId);
        await CreateInvestigationAsync(fixture.Store, createdId);

        var result = await fixture.Store.ClearHistoryAsync();

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.BaselineProtectedCount);
        Assert.Equal(2, result.NonTerminalPreservedCount);
        Assert.Null(await fixture.Store.GetAsync(eligibleId));
        Assert.NotNull(await fixture.Store.GetAsync(protectedId));
        Assert.NotNull(await fixture.Store.GetAsync(runningId));
        Assert.NotNull(await fixture.Store.GetAsync(createdId));
        Assert.Equal(1, fixture.CountRows("Baselines"));
        Assert.Equal(protectedId, Assert.Single(await fixture.Store.ListBaselinesAsync()).SourceInvestigationId);
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task ClearHistoryReturnsZeroForEmptyHistory()
    {
        using var fixture = new DatabaseFixture();

        var result = await fixture.Store.ClearHistoryAsync();

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.BaselineProtectedCount);
        Assert.Equal(0, result.NonTerminalPreservedCount);
    }

    [Fact]
    public async Task ClearHistoryPreservesAllBaselineProtectedInvestigations()
    {
        using var fixture = new DatabaseFixture();
        var sourceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var sourceId in sourceIds)
        {
            await PrepareInvestigationAsync(fixture.Store, sourceId);
            var execution = CreateExecution(fixture);
            await fixture.Store.AppendStepExecutionAsync(sourceId, execution);
            await CommitTerminalAsync(
                fixture.Store,
                sourceId,
                InvestigationLifecycleStatus.Completed,
                new InvestigationOutcome(FinalAnswer: "Protected."));
            await fixture.Store.CreateAsync(CreateBaseline(sourceId, "step-1", execution.Result!));
        }

        var result = await fixture.Store.ClearHistoryAsync();

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(2, result.BaselineProtectedCount);
        Assert.Equal(0, result.NonTerminalPreservedCount);
        Assert.Equal(2, fixture.CountRows("Investigations"));
        Assert.Equal(2, fixture.CountRows("Baselines"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task RollsBackSingleInvestigationDeletionWhenDependentDeleteFails()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        await fixture.Store.AppendStepExecutionAsync(
            fixture.InvestigationId,
            CreateExecution(fixture));
        await CommitCompletedAsync(fixture);
        InstallInvestigationDeleteFailureTrigger(fixture);

        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId));

        Assert.Equal(1, fixture.CountRows("Investigations"));
        Assert.Equal(1, fixture.CountRows("Plans"));
        Assert.Equal(1, fixture.CountRows("PlanSteps"));
        Assert.Equal(1, fixture.CountRows("StepExecutions"));
        Assert.Equal(1, fixture.CountRows("Observations"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task RollsBackClearHistoryWhenDependentDeleteFails()
    {
        using var fixture = new DatabaseFixture();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await PrepareInvestigationAsync(fixture.Store, firstId);
        await CommitTerminalAsync(
            fixture.Store,
            firstId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "First."));
        await PrepareInvestigationAsync(fixture.Store, secondId);
        await CommitTerminalAsync(
            fixture.Store,
            secondId,
            InvestigationLifecycleStatus.Failed,
            new InvestigationOutcome(FailureCode: "failed"));
        InstallInvestigationDeleteFailureTrigger(fixture);

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Store.ClearHistoryAsync());

        Assert.Equal(2, fixture.CountRows("Investigations"));
        Assert.Equal(2, fixture.CountRows("Plans"));
        Assert.Equal(2, fixture.CountRows("PlanSteps"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task DeletesBaselineWithoutChangingSourceOrUnrelatedHistory()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var sourceExecution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, sourceExecution);
        await CommitCompletedAsync(fixture);

        var unrelatedId = Guid.NewGuid();
        await PrepareInvestigationAsync(fixture.Store, unrelatedId);
        await CommitTerminalAsync(
            fixture.Store,
            unrelatedId,
            InvestigationLifecycleStatus.Failed,
            new InvestigationOutcome(FailureCode: "unrelated"));
        await fixture.Store.CreateAsync(
            CreateBaseline(fixture.InvestigationId, "step-1", sourceExecution.Result!));
        var baseline = Assert.Single(await fixture.Store.ListBaselinesAsync());

        var result = await fixture.Store.DeleteBaselineAsync(baseline.BaselineId);

        Assert.Equal(BaselineDeletionStatus.Deleted, result.Status);
        Assert.NotNull(await fixture.Store.GetAsync(fixture.InvestigationId));
        Assert.NotNull(await fixture.Store.GetAsync(unrelatedId));
        Assert.Empty(await fixture.Store.ListBaselinesAsync());
        Assert.Equal(2, fixture.CountRows("Investigations"));
        Assert.Equal(2, fixture.CountRows("Plans"));
        Assert.Equal(1, fixture.CountRows("Observations"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task BaselineDeletionReturnsNotFoundForUnknownAndDuplicateIds()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);
        await fixture.Store.CreateAsync(
            CreateBaseline(fixture.InvestigationId, "step-1", execution.Result!));
        var baseline = Assert.Single(await fixture.Store.ListBaselinesAsync());

        var unknownResult = await fixture.Store.DeleteBaselineAsync(Guid.NewGuid());
        var firstResult = await fixture.Store.DeleteBaselineAsync(baseline.BaselineId);
        var secondResult = await fixture.Store.DeleteBaselineAsync(baseline.BaselineId);

        Assert.Equal(BaselineDeletionStatus.NotFound, unknownResult.Status);
        Assert.Equal(BaselineDeletionStatus.Deleted, firstResult.Status);
        Assert.Equal(BaselineDeletionStatus.NotFound, secondResult.Status);
        Assert.NotNull(await fixture.Store.GetAsync(fixture.InvestigationId));
    }

    [Fact]
    public async Task DeletingOneOfMultipleBaselinesKeepsSourceProtectedUntilFinalReferenceIsRemoved()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);
        await fixture.Store.CreateAsync(
            CreateBaseline(fixture.InvestigationId, "step-1", execution.Result!));
        await fixture.Store.CreateAsync(
            CreateBaseline(fixture.InvestigationId, "step-1", execution.Result!));
        var baselines = (await fixture.Store.ListBaselinesAsync()).ToArray();

        var firstResult = await fixture.Store.DeleteBaselineAsync(baselines[0].BaselineId);
        var protectedResult = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);
        var remainingBaseline = Assert.Single(await fixture.Store.ListBaselinesAsync());
        var secondResult = await fixture.Store.DeleteBaselineAsync(remainingBaseline.BaselineId);
        var deleteSourceResult = await fixture.Store.DeleteInvestigationAsync(fixture.InvestigationId);

        Assert.Equal(BaselineDeletionStatus.Deleted, firstResult.Status);
        Assert.Equal(InvestigationDeletionStatus.BaselineProtected, protectedResult.Status);
        Assert.Equal(BaselineDeletionStatus.Deleted, secondResult.Status);
        Assert.Equal(InvestigationDeletionStatus.Deleted, deleteSourceResult.Status);
        Assert.Null(await fixture.Store.GetAsync(fixture.InvestigationId));
        Assert.Empty(await fixture.Store.ListBaselinesAsync());
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task RollsBackBaselineDeletionWhenTheDeleteFails()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);
        await fixture.Store.CreateAsync(
            CreateBaseline(fixture.InvestigationId, "step-1", execution.Result!));
        var baseline = Assert.Single(await fixture.Store.ListBaselinesAsync());
        InstallBaselineDeleteFailureTrigger(fixture);

        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Store.DeleteBaselineAsync(baseline.BaselineId));

        Assert.Single(await fixture.Store.ListBaselinesAsync());
        Assert.NotNull(await fixture.Store.GetAsync(fixture.InvestigationId));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task ClearHistoryCanDeleteSourceAfterItsFinalBaselineIsDeleted()
    {
        using var fixture = await CreatePreparedFixtureAsync();
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(fixture.InvestigationId, execution);
        await CommitCompletedAsync(fixture);
        await fixture.Store.CreateAsync(
            CreateBaseline(fixture.InvestigationId, "step-1", execution.Result!));
        var baseline = Assert.Single(await fixture.Store.ListBaselinesAsync());

        Assert.Equal(
            BaselineDeletionStatus.Deleted,
            (await fixture.Store.DeleteBaselineAsync(baseline.BaselineId)).Status);
        var result = await fixture.Store.ClearHistoryAsync();

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, result.BaselineProtectedCount);
        Assert.Null(await fixture.Store.GetAsync(fixture.InvestigationId));
        Assert.Empty(await fixture.Store.ListBaselinesAsync());
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task ClearSavedHistoryAndBaselinesDeletesAllSavedTerminalDataAndPreservesNonTerminalData()
    {
        using var fixture = new DatabaseFixture();
        var eligibleId = Guid.NewGuid();
        var protectedId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var createdId = Guid.NewGuid();

        await PrepareInvestigationAsync(fixture.Store, eligibleId);
        await CommitTerminalAsync(
            fixture.Store,
            eligibleId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Eligible."));

        await PrepareInvestigationAsync(fixture.Store, protectedId);
        var protectedExecution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(protectedId, protectedExecution);
        await CommitTerminalAsync(
            fixture.Store,
            protectedId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Protected."));
        await fixture.Store.CreateAsync(
            CreateBaseline(protectedId, "step-1", protectedExecution.Result!));
        await fixture.Store.CreateAsync(
            CreateBaseline(protectedId, "step-1", protectedExecution.Result!));

        await PrepareInvestigationAsync(fixture.Store, runningId);
        await CreateInvestigationAsync(fixture.Store, createdId);

        var result = await fixture.Store.ClearSavedHistoryAndBaselinesAsync();

        Assert.Equal(2, result.BaselinesDeletedCount);
        Assert.Equal(2, result.InvestigationsDeletedCount);
        Assert.Equal(2, result.NonTerminalPreservedCount);
        Assert.Equal(0, fixture.CountRows("Baselines"));
        Assert.Null(await fixture.Store.GetAsync(eligibleId));
        Assert.Null(await fixture.Store.GetAsync(protectedId));
        Assert.NotNull(await fixture.Store.GetAsync(runningId));
        Assert.NotNull(await fixture.Store.GetAsync(createdId));
        Assert.Equal(2, fixture.CountRows("Investigations"));
        Assert.Equal(1, fixture.CountRows("Plans"));
        Assert.Equal(0, fixture.CountRows("StepExecutions"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task ClearSavedHistoryAndBaselinesHandlesOnlyBaselineBackedSourceData()
    {
        using var fixture = new DatabaseFixture();
        var sourceId = Guid.NewGuid();
        await PrepareInvestigationAsync(fixture.Store, sourceId);
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(sourceId, execution);
        await CommitTerminalAsync(
            fixture.Store,
            sourceId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Source."));
        await fixture.Store.CreateAsync(CreateBaseline(sourceId, "step-1", execution.Result!));

        var result = await fixture.Store.ClearSavedHistoryAndBaselinesAsync();

        Assert.Equal(1, result.BaselinesDeletedCount);
        Assert.Equal(1, result.InvestigationsDeletedCount);
        Assert.Equal(0, result.NonTerminalPreservedCount);
        Assert.Equal(0, fixture.CountRows("Investigations"));
        Assert.Equal(0, fixture.CountRows("Baselines"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task ClearSavedHistoryAndBaselinesHandlesNonTerminalOnlyData()
    {
        using var fixture = new DatabaseFixture();
        var runningId = Guid.NewGuid();
        var createdId = Guid.NewGuid();
        await PrepareInvestigationAsync(fixture.Store, runningId);
        await CreateInvestigationAsync(fixture.Store, createdId);

        var result = await fixture.Store.ClearSavedHistoryAndBaselinesAsync();

        Assert.Equal(0, result.BaselinesDeletedCount);
        Assert.Equal(0, result.InvestigationsDeletedCount);
        Assert.Equal(2, result.NonTerminalPreservedCount);
        Assert.Equal(2, fixture.CountRows("Investigations"));
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task ClearSavedHistoryAndBaselinesReturnsZeroForEmptyStorage()
    {
        using var fixture = new DatabaseFixture();

        var result = await fixture.Store.ClearSavedHistoryAndBaselinesAsync();

        Assert.Equal(0, result.BaselinesDeletedCount);
        Assert.Equal(0, result.InvestigationsDeletedCount);
        Assert.Equal(0, result.NonTerminalPreservedCount);
        Assert.Equal(5, fixture.GetSchemaVersion());
        AssertNoForeignKeyViolations(fixture);
    }

    [Fact]
    public async Task RollsBackClearSavedHistoryAndBaselinesWhenTerminalCleanupFails()
    {
        using var fixture = new DatabaseFixture();
        var sourceId = Guid.NewGuid();
        await PrepareInvestigationAsync(fixture.Store, sourceId);
        var execution = CreateExecution(fixture);
        await fixture.Store.AppendStepExecutionAsync(sourceId, execution);
        await CommitTerminalAsync(
            fixture.Store,
            sourceId,
            InvestigationLifecycleStatus.Completed,
            new InvestigationOutcome(FinalAnswer: "Source."));
        await fixture.Store.CreateAsync(CreateBaseline(sourceId, "step-1", execution.Result!));
        InstallInvestigationDeleteFailureTrigger(fixture);

        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Store.ClearSavedHistoryAndBaselinesAsync());

        Assert.Equal(1, fixture.CountRows("Baselines"));
        Assert.Equal(1, fixture.CountRows("Investigations"));
        Assert.Equal(1, fixture.CountRows("Plans"));
        Assert.Equal(1, fixture.CountRows("StepExecutions"));
        AssertNoForeignKeyViolations(fixture);
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

    private static void InstallInvestigationDeleteFailureTrigger(DatabaseFixture fixture)
    {
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER FailInvestigationDelete
            BEFORE DELETE ON Plans
            BEGIN
                SELECT RAISE(ABORT, 'forced investigation delete failure');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void InstallBaselineDeleteFailureTrigger(DatabaseFixture fixture)
    {
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER FailBaselineDelete
            BEFORE DELETE ON Baselines
            BEGIN
                SELECT RAISE(ABORT, 'forced baseline delete failure');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void AssertNoForeignKeyViolations(DatabaseFixture fixture)
    {
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
    }

    private static Baseline CreateBaseline(
        DatabaseFixture fixture,
        string sourceStepId,
        ObservationResult observation) =>
        CreateBaseline(fixture.InvestigationId, sourceStepId, observation);

    private static Baseline CreateBaseline(
        Guid investigationId,
        string sourceStepId,
        ObservationResult observation) =>
        new(
            Guid.NewGuid(),
            investigationId,
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
        await CommitTerminalAsync(fixture.Store, fixture.InvestigationId, status, outcome);
    }

    private static async Task CommitTerminalAsync(
        SqliteInvestigationStore store,
        Guid investigationId,
        InvestigationLifecycleStatus status,
        InvestigationOutcome outcome)
    {
        Assert.True(await store.CommitTerminalOutcomeAsync(
            investigationId,
            status,
            outcome,
            DateTimeOffset.UtcNow));
    }

    private static async Task CreateInvestigationAsync(
        SqliteInvestigationStore store,
        Guid investigationId)
    {
        await store.CreateAsync(new Investigation(
            investigationId,
            "Question",
            "Question",
            DateTimeOffset.UtcNow,
            null,
            null,
            InvestigationLifecycleStatus.Created,
            null,
            [],
            []));
    }

    private static async Task PrepareInvestigationAsync(
        SqliteInvestigationStore store,
        Guid investigationId,
        params string[] stepIds)
    {
        var preparedStepIds = stepIds.Length == 0 ? ["step-1"] : stepIds;
        var createdAt = DateTimeOffset.UtcNow;
        await CreateInvestigationAsync(store, investigationId);
        await store.MarkRunningAsync(investigationId, createdAt.AddSeconds(1));
        await store.AppendPlanAsync(
            investigationId,
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
            await PrepareInvestigationAsync(fixture.Store, fixture.InvestigationId, stepIds);
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

        public int GetSchemaVersion()
        {
            using var connection = new SqliteConnection($"Data Source={Database.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(Version) FROM SchemaVersions;";
            return Convert.ToInt32(command.ExecuteScalar());
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
