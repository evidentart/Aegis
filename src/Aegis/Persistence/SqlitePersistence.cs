using System.Globalization;
using Aegis.Core;
using Microsoft.Data.Sqlite;

namespace Aegis.Persistence;

public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly object _initializationLock = new();
    private bool _initialized;

    public SqliteDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The database path must include a directory.");
            }

            Directory.CreateDirectory(directory);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, transaction, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(
                connection,
                transaction,
                "CREATE TABLE IF NOT EXISTS SchemaVersions (Version INTEGER NOT NULL);");

            var version = Convert.ToInt32(
                ExecuteScalar(connection, transaction, "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersions"),
                CultureInfo.InvariantCulture);
            if (version > 5)
            {
                throw new InvalidOperationException($"The database schema version '{version}' is newer than this application supports.");
            }

            if (version < 1)
            {
                ExecuteNonQuery(connection, transaction, """
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
                ExecuteNonQuery(connection, transaction, "INSERT INTO SchemaVersions (Version) VALUES (1);");
            }

            if (version < 2)
            {
                ExecuteNonQuery(connection, transaction, """
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

            if (version < 3)
            {
                ExecuteNonQuery(connection, transaction, """
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

            if (version < 4)
            {
                ExecuteNonQuery(connection, transaction, """
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

            if (version < 5)
            {
                ExecuteNonQuery(connection, transaction, """
                    CREATE TABLE WindowsRecentErrorEventSnapshots (
                        ObservationId INTEGER PRIMARY KEY NOT NULL,
                        WindowStartUtc TEXT NOT NULL,
                        WindowEndUtc TEXT NOT NULL,
                        IsTruncated INTEGER NOT NULL,
                        CHECK (IsTruncated IN (0, 1)),
                        FOREIGN KEY (ObservationId) REFERENCES Observations(ObservationId)
                    );
                    CREATE TABLE WindowsRecentErrorEventEntries (
                        ObservationId INTEGER NOT NULL,
                        Ordinal INTEGER NOT NULL,
                        ChannelKind TEXT NOT NULL,
                        ProviderName TEXT NOT NULL,
                        EventId INTEGER NOT NULL,
                        Severity TEXT NOT NULL,
                        OccurredAtUtc TEXT NOT NULL,
                        CHECK (Ordinal >= 0 AND Ordinal < 20),
                        CHECK (ChannelKind IN ('System', 'Application')),
                        CHECK (length(ProviderName) > 0 AND length(ProviderName) <= 256),
                        CHECK (EventId >= 0 AND EventId <= 65535),
                        CHECK (Severity IN ('Critical', 'Error')),
                        PRIMARY KEY (ObservationId, Ordinal),
                        FOREIGN KEY (ObservationId) REFERENCES WindowsRecentErrorEventSnapshots(ObservationId)
                    );
                    CREATE INDEX IX_WindowsRecentErrorEventEntries_Observation
                        ON WindowsRecentErrorEventEntries(ObservationId, Ordinal);
                    INSERT INTO SchemaVersions (Version) VALUES (5);
                    """);
            }

            transaction.Commit();
            _initialized = true;
        }
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecuteNonQuery(connection, null, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    internal static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    internal static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    internal static object? ExecuteScalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command.ExecuteScalar();
    }
}

public sealed class SqliteInvestigationStore : IInvestigationHistoryStore, IBaselineStore
{
    private const string DateFormat = "O";
    private const string ObservedFactsSection = "ObservedFacts";
    private const string ConclusionsSection = "Conclusions";
    private const string HypothesesSection = "Hypotheses";
    private const string UncertaintiesSection = "Uncertainties";
    private const string RecommendationsSection = "Recommendations";
    private readonly SqliteDatabase _database;

    private sealed record PersistedReportStatement(
        string SectionKind,
        int StatementOrdinal,
        string Text);

    private sealed record PersistedReportReference(
        string SectionKind,
        int StatementOrdinal,
        int ReferenceOrdinal,
        string StepId);

    public SqliteInvestigationStore(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public void Initialize() => _database.Initialize();

    public Task CreateAsync(
        Investigation investigation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Investigations
                (InvestigationId, Question, Objective, CreatedAtUtc, LifecycleStatus)
            VALUES ($id, $question, $objective, $created, $status);
            """;
        SqliteDatabase.AddParameter(command, "$id", investigation.InvestigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$question", investigation.Question);
        SqliteDatabase.AddParameter(command, "$objective", investigation.Objective);
        SqliteDatabase.AddParameter(command, "$created", FormatDate(investigation.CreatedAtUtc));
        SqliteDatabase.AddParameter(command, "$status", (int)InvestigationLifecycleStatus.Created);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task MarkRunningAsync(
        Guid investigationId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Investigations
            SET StartedAtUtc = $started, LifecycleStatus = $running
            WHERE InvestigationId = $id AND LifecycleStatus = $created;
            """;
        SqliteDatabase.AddParameter(command, "$started", FormatDate(startedAtUtc));
        SqliteDatabase.AddParameter(command, "$running", (int)InvestigationLifecycleStatus.Running);
        SqliteDatabase.AddParameter(command, "$created", (int)InvestigationLifecycleStatus.Created);
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvestigationPersistenceException("The investigation could not be marked as running.");
        }

        return Task.CompletedTask;
    }

    public Task AppendPlanAsync(
        Guid investigationId,
        InvestigationPlanHistoryEntry plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Plans (InvestigationId, PlanSequence, AcceptedAtUtc, Objective)
                SELECT $id, $sequence, $accepted, $objective
                WHERE EXISTS (
                    SELECT 1 FROM Investigations
                    WHERE InvestigationId = $id AND LifecycleStatus = $running);
                """;
            SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$sequence", plan.PlanSequence);
            SqliteDatabase.AddParameter(command, "$accepted", FormatDate(plan.AcceptedAtUtc));
            SqliteDatabase.AddParameter(command, "$objective", plan.Plan.Objective);
            SqliteDatabase.AddParameter(command, "$running", (int)InvestigationLifecycleStatus.Running);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvestigationPersistenceException(
                    "Plans can only be appended while the investigation is running.");
            }
        }

        for (var index = 0; index < plan.Plan.Steps.Count; index++)
        {
            var step = plan.Plan.Steps[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO PlanSteps
                    (InvestigationId, PlanSequence, StepId, Ordinal, ToolId, Status)
                VALUES ($id, $sequence, $step, $ordinal, $tool, $status);
                """;
            SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$sequence", plan.PlanSequence);
            SqliteDatabase.AddParameter(command, "$step", step.StepId);
            SqliteDatabase.AddParameter(command, "$ordinal", index);
            SqliteDatabase.AddParameter(command, "$tool", step.ToolId);
            SqliteDatabase.AddParameter(command, "$status", (int)step.Status);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task AppendStepExecutionAsync(
        Guid investigationId,
        InvestigationStepExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Result is { } result &&
            (result.RequestId != execution.RequestId ||
             !string.Equals(result.ToolId, execution.ToolId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The execution and observation result identities must match.");
        }

        ValidateExecutionResult(execution);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureInvestigationRunning(connection, transaction, investigationId);
        var planStep = ReadPlanStep(connection, transaction, investigationId, execution);
        if (!string.Equals(planStep.ToolId, execution.ToolId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The execution ToolId does not match the persisted plan step.");
        }

        if (planStep.Status != InvestigationStepStatus.Pending)
        {
            throw new InvestigationPersistenceException("The investigation step is no longer pending.");
        }

        long? observationId = null;

        if (execution.Result is not null)
        {
            observationId = InsertObservation(connection, transaction, investigationId, execution);
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO StepExecutions
                    (InvestigationId, PlanSequence, StepId, ToolId, RequestId,
                     RequestedAtUtc, ObservedAtUtc, ToolContractVersion, Status, ObservationId)
                VALUES ($id, $sequence, $step, $tool, $request, $requested,
                        $observed, $version, $status, $observation);
                """;
            SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$sequence", execution.PlanSequence);
            SqliteDatabase.AddParameter(command, "$step", execution.StepId);
            SqliteDatabase.AddParameter(command, "$tool", execution.ToolId);
            SqliteDatabase.AddParameter(command, "$request", execution.RequestId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$requested", FormatDate(execution.RequestedAtUtc));
            SqliteDatabase.AddParameter(command, "$observed", FormatDateOrNull(execution.ObservedAtUtc));
            SqliteDatabase.AddParameter(command, "$version", execution.ToolContractVersion);
            SqliteDatabase.AddParameter(command, "$status", (int)execution.Status);
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE PlanSteps
                SET Status = $status
                WHERE InvestigationId = $id AND PlanSequence = $sequence AND StepId = $step
                  AND ToolId = $tool
                  AND Status = $pending
                  AND EXISTS (
                      SELECT 1 FROM Investigations
                      WHERE InvestigationId = $id AND LifecycleStatus = $running);
                """;
            SqliteDatabase.AddParameter(command, "$status", (int)execution.Status);
            SqliteDatabase.AddParameter(command, "$pending", (int)InvestigationStepStatus.Pending);
            SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$sequence", execution.PlanSequence);
            SqliteDatabase.AddParameter(command, "$step", execution.StepId);
            SqliteDatabase.AddParameter(command, "$tool", execution.ToolId);
            SqliteDatabase.AddParameter(command, "$running", (int)InvestigationLifecycleStatus.Running);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvestigationPersistenceException("The investigation step could not be updated.");
            }
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task MarkStepsSkippedAsync(
        Guid investigationId,
        int planSequence,
        IReadOnlyList<string> stepIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stepIds);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureInvestigationRunning(connection, transaction, investigationId);
        foreach (var stepId in stepIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE PlanSteps
                SET Status = $skipped
                WHERE InvestigationId = $id AND PlanSequence = $sequence
                  AND StepId = $step AND Status = $pending
                  AND EXISTS (
                      SELECT 1 FROM Investigations
                      WHERE InvestigationId = $id AND LifecycleStatus = $running);
                """;
            SqliteDatabase.AddParameter(command, "$skipped", (int)InvestigationStepStatus.Skipped);
            SqliteDatabase.AddParameter(command, "$pending", (int)InvestigationStepStatus.Pending);
            SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$sequence", planSequence);
            SqliteDatabase.AddParameter(command, "$step", stepId);
            SqliteDatabase.AddParameter(command, "$running", (int)InvestigationLifecycleStatus.Running);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvestigationPersistenceException("An unreached investigation step could not be marked as skipped.");
            }
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<bool> CommitTerminalOutcomeAsync(
        Guid investigationId,
        InvestigationLifecycleStatus terminalStatus,
        InvestigationOutcome outcome,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (terminalStatus is not (InvestigationLifecycleStatus.Completed or
            InvestigationLifecycleStatus.Failed or InvestigationLifecycleStatus.Cancelled))
        {
            throw new ArgumentException("A terminal lifecycle status is required.", nameof(terminalStatus));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Investigations
            SET CompletedAtUtc = $completed,
                LifecycleStatus = $status,
                FinalAnswer = $answer,
                FailureCode = $failureCode,
                FailureMessage = $failureMessage
            WHERE InvestigationId = $id AND LifecycleStatus = $running;
            """;
        SqliteDatabase.AddParameter(command, "$completed", FormatDate(completedAtUtc));
        SqliteDatabase.AddParameter(command, "$status", (int)terminalStatus);
        SqliteDatabase.AddParameter(command, "$answer", outcome.FinalAnswer);
        SqliteDatabase.AddParameter(command, "$failureCode", outcome.FailureCode);
        SqliteDatabase.AddParameter(command, "$failureMessage", outcome.FailureMessage);
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$running", (int)InvestigationLifecycleStatus.Running);
        var committed = command.ExecuteNonQuery() == 1;
        if (committed && outcome.Report is not null)
        {
            if (!string.Equals(outcome.FinalAnswer, outcome.Report.Summary, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The persisted final answer must match the structured report summary.");
            }

            InsertReport(connection, transaction, investigationId, outcome.Report);
        }

        transaction.Commit();
        return Task.FromResult(committed);
    }

    public Task<IReadOnlyList<InvestigationSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        var summaries = new List<InvestigationSummary>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT InvestigationId, Question, CreatedAtUtc, CompletedAtUtc,
                   LifecycleStatus, FinalAnswer
            FROM Investigations
            ORDER BY CreatedAtUtc DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            summaries.Add(new InvestigationSummary(
                ParseGuid(reader.GetString(0)),
                reader.GetString(1),
                ParseDate(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
                (InvestigationLifecycleStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return Task.FromResult<IReadOnlyList<InvestigationSummary>>(summaries);
    }

    public Task<InvestigationDetails?> GetAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        using var connection = _database.OpenConnection();
        Investigation? investigation;
        string question;
        string objective;
        DateTimeOffset createdAtUtc;
        DateTimeOffset? startedAtUtc;
        DateTimeOffset? completedAtUtc;
        InvestigationLifecycleStatus lifecycleStatus;
        InvestigationOutcome? outcome;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Question, Objective, CreatedAtUtc, StartedAtUtc, CompletedAtUtc,
                       LifecycleStatus, FinalAnswer, FailureCode, FailureMessage
                FROM Investigations WHERE InvestigationId = $id;
                """;
            SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult<InvestigationDetails?>(null);
            }

            question = reader.GetString(0);
            objective = reader.GetString(1);
            createdAtUtc = ParseDate(reader.GetString(2));
            startedAtUtc = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3));
            completedAtUtc = reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4));
            lifecycleStatus = (InvestigationLifecycleStatus)reader.GetInt32(5);
            outcome = CreateOutcome(reader, 6, 7, 8);
        }

        if (outcome is not null)
        {
            outcome = outcome with
            {
                Report = ReadReport(connection, investigationId, outcome.FinalAnswer)
            };
        }

        investigation = new Investigation(
            investigationId,
            question,
            objective,
            createdAtUtc,
            startedAtUtc,
            completedAtUtc,
            lifecycleStatus,
            outcome,
            ReadPlans(connection, investigationId),
            ReadExecutions(connection, investigationId));

        return Task.FromResult<InvestigationDetails?>(new InvestigationDetails(investigation));
    }

    public Task<InvestigationDeletionResult> DeleteInvestigationAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var lifecycleStatus = ReadInvestigationLifecycleStatus(
            connection,
            transaction,
            investigationId);
        if (lifecycleStatus is null)
        {
            transaction.Commit();
            return Task.FromResult(new InvestigationDeletionResult(
                investigationId,
                InvestigationDeletionStatus.NotFound));
        }

        if (lifecycleStatus is not (InvestigationLifecycleStatus.Completed or
            InvestigationLifecycleStatus.Failed or InvestigationLifecycleStatus.Cancelled))
        {
            transaction.Commit();
            return Task.FromResult(new InvestigationDeletionResult(
                investigationId,
                InvestigationDeletionStatus.NotTerminal));
        }

        if (HasBaselineReference(connection, transaction, investigationId))
        {
            transaction.Commit();
            return Task.FromResult(new InvestigationDeletionResult(
                investigationId,
                InvestigationDeletionStatus.BaselineProtected));
        }

        DeleteInvestigationOwnedRows(connection, transaction, investigationId);
        transaction.Commit();
        return Task.FromResult(new InvestigationDeletionResult(
            investigationId,
            InvestigationDeletionStatus.Deleted));
    }

    public Task<ClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var deletableInvestigationIds = new List<Guid>();
        var baselineProtectedCount = 0;
        var nonTerminalPreservedCount = 0;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT InvestigationId, LifecycleStatus,
                       EXISTS (
                           SELECT 1
                           FROM Baselines baseline
                           WHERE baseline.SourceInvestigationId = investigation.InvestigationId) AS HasBaseline
                FROM Investigations investigation;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = ParseGuid(reader.GetString(0));
                var lifecycleStatus = (InvestigationLifecycleStatus)reader.GetInt32(1);
                var hasBaseline = reader.GetInt32(2) != 0;
                if (hasBaseline)
                {
                    baselineProtectedCount++;
                }
                else if (lifecycleStatus is InvestigationLifecycleStatus.Completed or
                    InvestigationLifecycleStatus.Failed or InvestigationLifecycleStatus.Cancelled)
                {
                    deletableInvestigationIds.Add(id);
                }
                else
                {
                    nonTerminalPreservedCount++;
                }
            }
        }

        foreach (var investigationId in deletableInvestigationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteInvestigationOwnedRows(connection, transaction, investigationId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(new ClearHistoryResult(
            deletableInvestigationIds.Count,
            baselineProtectedCount,
            nonTerminalPreservedCount));
    }

    public Task<ClearSavedHistoryAndBaselinesResult> ClearSavedHistoryAndBaselinesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        int baselinesDeletedCount;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Baselines;";
            baselinesDeletedCount = command.ExecuteNonQuery();
        }

        var terminalInvestigationIds = new List<Guid>();
        var nonTerminalPreservedCount = 0;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT InvestigationId, LifecycleStatus
                FROM Investigations;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var investigationId = ParseGuid(reader.GetString(0));
                var lifecycleStatus = (InvestigationLifecycleStatus)reader.GetInt32(1);
                if (lifecycleStatus is InvestigationLifecycleStatus.Completed or
                    InvestigationLifecycleStatus.Failed or InvestigationLifecycleStatus.Cancelled)
                {
                    terminalInvestigationIds.Add(investigationId);
                }
                else
                {
                    nonTerminalPreservedCount++;
                }
            }
        }

        foreach (var investigationId in terminalInvestigationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteInvestigationOwnedRows(connection, transaction, investigationId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(new ClearSavedHistoryAndBaselinesResult(
            baselinesDeletedCount,
            terminalInvestigationIds.Count,
            nonTerminalPreservedCount));
    }

    public Task CreateAsync(
        Baseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        if (!string.Equals(baseline.ToolId, WindowsSystemInfoObservationTool.ToolId, StringComparison.Ordinal) ||
            baseline.Observation.Status != ObservationStatus.Succeeded ||
            baseline.Observation.Data is not WindowsSystemInfo data)
        {
            throw new InvalidOperationException("Only approved Windows system information can be baselined.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var sourceCommand = connection.CreateCommand())
        {
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = """
                SELECT observation.RequestId, observation.ToolId, observation.ObservedAtUtc,
                       observation.Status, observation.FailureCode, observation.FailureMessage,
                       observation.Platform, observation.OsVersion, observation.Build,
                       observation.Architecture
                FROM Investigations investigation
                INNER JOIN StepExecutions execution
                    ON execution.InvestigationId = investigation.InvestigationId
                INNER JOIN PlanSteps step
                    ON step.InvestigationId = execution.InvestigationId
                   AND step.PlanSequence = execution.PlanSequence
                   AND step.StepId = execution.StepId
                INNER JOIN Observations observation
                    ON observation.ObservationId = execution.ObservationId
                WHERE investigation.InvestigationId = $investigation
                  AND investigation.LifecycleStatus = $completedInvestigation
                  AND execution.StepId = $sourceStep
                  AND execution.RequestId = $request
                  AND execution.ToolId = $tool
                  AND execution.Status = $completedExecution
                  AND step.ToolId = execution.ToolId
                  AND step.Status = $completedStep
                  AND observation.InvestigationId = execution.InvestigationId
                  AND observation.PlanSequence = execution.PlanSequence
                  AND observation.StepId = execution.StepId
                  AND observation.RequestId = execution.RequestId
                  AND observation.ToolId = execution.ToolId
                  AND observation.RequestId = $request
                  AND observation.ToolId = $tool
                ORDER BY execution.ExecutionId;
                """;
            SqliteDatabase.AddParameter(sourceCommand, "$investigation", baseline.SourceInvestigationId.ToString("D"));
            SqliteDatabase.AddParameter(sourceCommand, "$sourceStep", baseline.SourceStepId);
            SqliteDatabase.AddParameter(sourceCommand, "$request", baseline.Observation.RequestId.ToString("D"));
            SqliteDatabase.AddParameter(sourceCommand, "$tool", baseline.ToolId);
            SqliteDatabase.AddParameter(sourceCommand, "$completedInvestigation", (int)InvestigationLifecycleStatus.Completed);
            SqliteDatabase.AddParameter(sourceCommand, "$completedExecution", (int)InvestigationStepStatus.Completed);
            SqliteDatabase.AddParameter(sourceCommand, "$completedStep", (int)InvestigationStepStatus.Completed);
            using var reader = sourceCommand.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "The baseline source must identify one completed persisted observation with matching data.");
            }

            var matchesBaselineObservation = MatchesBaselineObservation(baseline.Observation, reader);
            if (reader.Read() || !matchesBaselineObservation)
            {
                throw new InvalidOperationException(
                    "The baseline source must identify one completed persisted observation with matching data.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Baselines
                    (BaselineId, SourceInvestigationId, SourceStepId, ToolId, CreatedAtUtc,
                     RequestId, ObservedAtUtc, Platform, OsVersion, Build, Architecture)
                VALUES ($id, $investigation, $step, $tool, $created,
                        $request, $observed, $platform, $osVersion, $build, $architecture);
                """;
            SqliteDatabase.AddParameter(command, "$id", baseline.BaselineId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$investigation", baseline.SourceInvestigationId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$step", baseline.SourceStepId);
            SqliteDatabase.AddParameter(command, "$tool", baseline.ToolId);
            SqliteDatabase.AddParameter(command, "$created", FormatDate(baseline.CreatedAtUtc));
            SqliteDatabase.AddParameter(command, "$request", baseline.Observation.RequestId.ToString("D"));
            SqliteDatabase.AddParameter(command, "$observed", FormatDate(baseline.Observation.ObservedAtUtc));
            SqliteDatabase.AddParameter(command, "$platform", data.Platform);
            SqliteDatabase.AddParameter(command, "$osVersion", data.OsVersion);
            SqliteDatabase.AddParameter(command, "$build", data.Build);
            SqliteDatabase.AddParameter(command, "$architecture", data.Architecture);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    private static bool MatchesBaselineObservation(
        ObservationResult expected,
        SqliteDataReader persisted)
    {
        if (expected.RequestId != ParseGuid(persisted.GetString(0)) ||
            !string.Equals(expected.ToolId, persisted.GetString(1), StringComparison.Ordinal) ||
            expected.ObservedAtUtc.ToUniversalTime() != ParseDate(persisted.GetString(2)) ||
            expected.Status != (ObservationStatus)persisted.GetInt32(3))
        {
            return false;
        }

        var persistedFailureCode = persisted.IsDBNull(4) ? null : persisted.GetString(4);
        var persistedFailureMessage = persisted.IsDBNull(5) ? null : persisted.GetString(5);
        if (!string.Equals(expected.Failure?.Code, persistedFailureCode, StringComparison.Ordinal) ||
            !string.Equals(expected.Failure?.Message, persistedFailureMessage, StringComparison.Ordinal))
        {
            return false;
        }

        if (expected.Data is not WindowsSystemInfo expectedData ||
            persisted.IsDBNull(6) ||
            persisted.IsDBNull(7) ||
            persisted.IsDBNull(9))
        {
            return false;
        }

        return string.Equals(expectedData.Platform, persisted.GetString(6), StringComparison.Ordinal) &&
               string.Equals(expectedData.OsVersion, persisted.GetString(7), StringComparison.Ordinal) &&
               expectedData.Build == (persisted.IsDBNull(8) ? null : persisted.GetInt32(8)) &&
               string.Equals(expectedData.Architecture, persisted.GetString(9), StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<BaselineSummary>> ListBaselinesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        var baselines = new List<BaselineSummary>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BaselineId, SourceInvestigationId, ToolId, CreatedAtUtc,
                   Platform, OsVersion, Build, Architecture
            FROM Baselines ORDER BY CreatedAtUtc DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            baselines.Add(new BaselineSummary(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                reader.GetString(2),
                ParseDate(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7)));
        }

        return Task.FromResult<IReadOnlyList<BaselineSummary>>(baselines);
    }

    public Task<BaselineDeletionResult> DeleteBaselineAsync(
        Guid baselineId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM Baselines
            WHERE BaselineId = $id;
            """;
        SqliteDatabase.AddParameter(command, "$id", baselineId.ToString("D"));
        var deleted = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return Task.FromResult(new BaselineDeletionResult(
            baselineId,
            deleted ? BaselineDeletionStatus.Deleted : BaselineDeletionStatus.NotFound));
    }

    private static InvestigationLifecycleStatus? ReadInvestigationLifecycleStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT LifecycleStatus
            FROM Investigations
            WHERE InvestigationId = $id;
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        var value = command.ExecuteScalar();
        return value is null
            ? null
            : (InvestigationLifecycleStatus)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool HasBaselineReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM Baselines
                WHERE SourceInvestigationId = $id);
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void DeleteInvestigationOwnedRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId)
    {
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM InvestigationReportEvidenceReferences
            WHERE InvestigationId = $id;
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM InvestigationReportStatements
            WHERE InvestigationId = $id;
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM StepExecutions
            WHERE InvestigationId = $id;
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM WindowsPerformanceTopProcessEntries
            WHERE ObservationId IN (
                SELECT ObservationId
                FROM Observations
                WHERE InvestigationId = $id);
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM WindowsPerformanceTopProcessSnapshots
            WHERE ObservationId IN (
                SELECT ObservationId
                FROM Observations
                WHERE InvestigationId = $id);
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM WindowsPerformanceSystemObservations
            WHERE ObservationId IN (
                SELECT ObservationId
                FROM Observations
                WHERE InvestigationId = $id);
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM WindowsRecentErrorEventEntries
            WHERE ObservationId IN (
                SELECT ObservationId
                FROM Observations
                WHERE InvestigationId = $id);
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM WindowsRecentErrorEventSnapshots
            WHERE ObservationId IN (
                SELECT ObservationId
                FROM Observations
                WHERE InvestigationId = $id);
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM Observations
            WHERE InvestigationId = $id;
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM PlanSteps
            WHERE InvestigationId = $id;
            """,
            investigationId);
        ExecuteInvestigationDelete(
            connection,
            transaction,
            """
            DELETE FROM Plans
            WHERE InvestigationId = $id;
            """,
            investigationId);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM Investigations
            WHERE InvestigationId = $id
              AND LifecycleStatus IN ($completed, $failed, $cancelled)
              AND NOT EXISTS (
                  SELECT 1
                  FROM Baselines
                  WHERE SourceInvestigationId = $id);
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$completed", (int)InvestigationLifecycleStatus.Completed);
        SqliteDatabase.AddParameter(command, "$failed", (int)InvestigationLifecycleStatus.Failed);
        SqliteDatabase.AddParameter(command, "$cancelled", (int)InvestigationLifecycleStatus.Cancelled);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvestigationPersistenceException(
                "The investigation could not be deleted because its persisted state changed.");
        }
    }

    private static void ExecuteInvestigationDelete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        Guid investigationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private long InsertObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId,
        InvestigationStepExecution execution)
    {
        var result = execution.Result!;
        ValidateObservationData(result);
        var data = result.Data as WindowsSystemInfo;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Observations
                (InvestigationId, PlanSequence, StepId, RequestId, ToolId, ObservedAtUtc,
                 Status, FailureCode, FailureMessage, Platform, OsVersion, Build, Architecture)
            VALUES ($id, $sequence, $step, $request, $tool, $observed,
                    $status, $failureCode, $failureMessage, $platform, $osVersion, $build, $architecture);
            SELECT last_insert_rowid();
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$sequence", execution.PlanSequence);
        SqliteDatabase.AddParameter(command, "$step", execution.StepId);
        SqliteDatabase.AddParameter(command, "$request", result.RequestId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$tool", result.ToolId);
        SqliteDatabase.AddParameter(command, "$observed", FormatDate(result.ObservedAtUtc));
        SqliteDatabase.AddParameter(command, "$status", (int)result.Status);
        SqliteDatabase.AddParameter(command, "$failureCode", result.Failure?.Code);
        SqliteDatabase.AddParameter(command, "$failureMessage", result.Failure?.Message);
        SqliteDatabase.AddParameter(command, "$platform", data?.Platform);
        SqliteDatabase.AddParameter(command, "$osVersion", data?.OsVersion);
        SqliteDatabase.AddParameter(command, "$build", data?.Build);
        SqliteDatabase.AddParameter(command, "$architecture", data?.Architecture);
        var observationId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        switch (result.Data)
        {
            case WindowsPerformanceSystem system:
                InsertPerformanceSystemObservation(connection, transaction, observationId, system);
                break;
            case WindowsPerformanceTopProcesses processes:
                InsertTopProcessObservation(connection, transaction, observationId, processes);
                break;
            case WindowsRecentErrorEvents recentErrors:
                InsertRecentErrorEventsObservation(connection, transaction, observationId, recentErrors);
                break;
        }

        return observationId;
    }

    private static void InsertReport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId,
        InvestigationReport report)
    {
        var evidenceStepIds = ReadSuccessfulEvidenceStepIds(connection, transaction, investigationId);
        InvestigationReportValidator.Validate(report, evidenceStepIds);

        var sections = new[]
        {
            (ObservedFactsSection, report.ObservedFacts),
            (ConclusionsSection, report.Conclusions),
            (HypothesesSection, report.Hypotheses),
            (UncertaintiesSection, report.Uncertainties.Select(text => new EvidenceStatement(text, Array.Empty<string>())).ToArray()),
            (RecommendationsSection, report.Recommendations.Select(text => new EvidenceStatement(text, Array.Empty<string>())).ToArray())
        };

        foreach (var (sectionKind, statements) in sections)
        {
            for (var statementOrdinal = 0; statementOrdinal < statements.Count; statementOrdinal++)
            {
                var statement = statements[statementOrdinal];
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO InvestigationReportStatements
                            (InvestigationId, SectionKind, StatementOrdinal, Text)
                        VALUES ($investigation, $section, $ordinal, $text);
                        """;
                    SqliteDatabase.AddParameter(command, "$investigation", investigationId.ToString("D"));
                    SqliteDatabase.AddParameter(command, "$section", sectionKind);
                    SqliteDatabase.AddParameter(command, "$ordinal", statementOrdinal);
                    SqliteDatabase.AddParameter(command, "$text", statement.Text);
                    command.ExecuteNonQuery();
                }

                for (var referenceOrdinal = 0; referenceOrdinal < statement.EvidenceStepIds.Count; referenceOrdinal++)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO InvestigationReportEvidenceReferences
                            (InvestigationId, SectionKind, StatementOrdinal, ReferenceOrdinal, StepId)
                        VALUES ($investigation, $section, $ordinal, $reference, $step);
                        """;
                    SqliteDatabase.AddParameter(command, "$investigation", investigationId.ToString("D"));
                    SqliteDatabase.AddParameter(command, "$section", sectionKind);
                    SqliteDatabase.AddParameter(command, "$ordinal", statementOrdinal);
                    SqliteDatabase.AddParameter(command, "$reference", referenceOrdinal);
                    SqliteDatabase.AddParameter(command, "$step", statement.EvidenceStepIds[referenceOrdinal]);
                    command.ExecuteNonQuery();
                }
            }
        }
    }

    private static InvestigationReport? ReadReport(
        SqliteConnection connection,
        Guid investigationId,
        string? finalAnswer)
    {
        var statements = new List<PersistedReportStatement>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT SectionKind, StatementOrdinal, Text
                FROM InvestigationReportStatements
                WHERE InvestigationId = $investigation
                ORDER BY SectionKind, StatementOrdinal;
                """;
            SqliteDatabase.AddParameter(command, "$investigation", investigationId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                statements.Add(new PersistedReportStatement(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2)));
            }
        }

        var references = new List<PersistedReportReference>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT SectionKind, StatementOrdinal, ReferenceOrdinal, StepId
                FROM InvestigationReportEvidenceReferences
                WHERE InvestigationId = $investigation
                ORDER BY SectionKind, StatementOrdinal, ReferenceOrdinal;
                """;
            SqliteDatabase.AddParameter(command, "$investigation", investigationId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                references.Add(new PersistedReportReference(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3)));
            }
        }

        if (finalAnswer is null)
        {
            if (statements.Count > 0 || references.Count > 0)
            {
                throw new InvestigationPersistenceException(
                    "Structured report rows exist without a persisted final answer.");
            }

            return null;
        }

        if (statements.Count == 0)
        {
            if (references.Count > 0)
            {
                throw new InvestigationPersistenceException(
                    "Report evidence references exist without report statements.");
            }

            return InvestigationReport.FromLegacySummary(finalAnswer);
        }

        foreach (var statement in statements)
        {
            if (!IsSupportedSection(statement.SectionKind))
            {
                throw new InvestigationPersistenceException(
                    $"The persisted report contains an unsupported section kind '{statement.SectionKind}'.");
            }
        }

        foreach (var reference in references)
        {
            if (!IsEvidenceSection(reference.SectionKind))
            {
                throw new InvestigationPersistenceException(
                    "The persisted report contains an evidence reference for a section that does not support references.");
            }

            if (!statements.Any(statement =>
                    string.Equals(statement.SectionKind, reference.SectionKind, StringComparison.Ordinal) &&
                    statement.StatementOrdinal == reference.StatementOrdinal))
            {
                throw new InvestigationPersistenceException(
                    "The persisted report contains an evidence reference for a missing statement.");
            }
        }

        var referencesByStatement = references
            .GroupBy(reference => (reference.SectionKind, reference.StatementOrdinal))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(reference => reference.ReferenceOrdinal).ToArray());
        var observedFacts = ReadEvidenceStatements(statements, referencesByStatement, ObservedFactsSection);
        var conclusions = ReadEvidenceStatements(statements, referencesByStatement, ConclusionsSection);
        var hypotheses = ReadEvidenceStatements(statements, referencesByStatement, HypothesesSection);
        var uncertainties = ReadTextStatements(statements, UncertaintiesSection);
        var recommendations = ReadTextStatements(statements, RecommendationsSection);
        var report = new InvestigationReport(
            finalAnswer,
            observedFacts,
            conclusions,
            hypotheses,
            uncertainties,
            recommendations);

        try
        {
            InvestigationReportValidator.Validate(
                report,
                ReadSuccessfulEvidenceStepIds(connection, transaction: null, investigationId));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentNullException)
        {
            throw new InvestigationPersistenceException(
                "The persisted investigation report is invalid.",
                exception);
        }

        return report;
    }

    private static IReadOnlyList<EvidenceStatement> ReadEvidenceStatements(
        IReadOnlyList<PersistedReportStatement> statements,
        IReadOnlyDictionary<(string SectionKind, int StatementOrdinal), PersistedReportReference[]> referencesByStatement,
        string sectionKind)
    {
        var sectionStatements = statements
            .Where(statement => string.Equals(statement.SectionKind, sectionKind, StringComparison.Ordinal))
            .OrderBy(statement => statement.StatementOrdinal)
            .ToArray();
        ValidateStatementOrdinals(sectionStatements, sectionKind);
        var result = new List<EvidenceStatement>();
        foreach (var statement in sectionStatements)
        {
            if (!referencesByStatement.TryGetValue((sectionKind, statement.StatementOrdinal), out var references))
            {
                throw new InvestigationPersistenceException(
                    $"The persisted {sectionKind} statement has no evidence references.");
            }

            ValidateReferenceOrdinals(references, sectionKind, statement.StatementOrdinal);
            result.Add(new EvidenceStatement(
                statement.Text,
                references.Select(reference => reference.StepId).ToArray()));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadTextStatements(
        IReadOnlyList<PersistedReportStatement> statements,
        string sectionKind)
    {
        var sectionStatements = statements
            .Where(statement => string.Equals(statement.SectionKind, sectionKind, StringComparison.Ordinal))
            .OrderBy(statement => statement.StatementOrdinal)
            .ToArray();
        ValidateStatementOrdinals(sectionStatements, sectionKind);
        return sectionStatements.Select(statement => statement.Text).ToArray();
    }

    private static void ValidateStatementOrdinals(
        IReadOnlyList<PersistedReportStatement> statements,
        string sectionKind)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            if (statements[index].StatementOrdinal != index)
            {
                throw new InvestigationPersistenceException(
                    $"The persisted {sectionKind} report statements have invalid ordinals.");
            }
        }
    }

    private static void ValidateReferenceOrdinals(
        IReadOnlyList<PersistedReportReference> references,
        string sectionKind,
        int statementOrdinal)
    {
        for (var index = 0; index < references.Count; index++)
        {
            if (references[index].ReferenceOrdinal != index)
            {
                throw new InvestigationPersistenceException(
                    $"The persisted {sectionKind} statement {statementOrdinal} has invalid evidence-reference ordinals.");
            }
        }
    }

    private static bool IsSupportedSection(string sectionKind) =>
        sectionKind is ObservedFactsSection or ConclusionsSection or HypothesesSection or
            UncertaintiesSection or RecommendationsSection;

    private static bool IsEvidenceSection(string sectionKind) =>
        sectionKind is ObservedFactsSection or ConclusionsSection or HypothesesSection;

    private static IReadOnlySet<string> ReadSuccessfulEvidenceStepIds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid investigationId)
    {
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT execution.StepId
            FROM StepExecutions execution
            INNER JOIN Observations observation
                ON observation.ObservationId = execution.ObservationId
            WHERE execution.InvestigationId = $investigation
              AND execution.ObservationId IS NOT NULL
              AND observation.Status = $succeeded;
            """;
        SqliteDatabase.AddParameter(command, "$investigation", investigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$succeeded", (int)ObservationStatus.Succeeded);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            stepIds.Add(reader.GetString(0));
        }

        return stepIds;
    }

    private static void ValidateObservationData(ObservationResult result)
    {
        if (result.Status != ObservationStatus.Succeeded && result.Data is not null)
        {
            throw new InvalidOperationException("A failed observation cannot contain typed observation data.");
        }

        switch (result.Data)
        {
            case null:
                return;
            case WindowsSystemInfo:
                EnsureObservationTool(result, WindowsSystemInfoObservationTool.ToolId);
                return;
            case WindowsPerformanceSystem system:
                EnsureObservationTool(result, WindowsPerformanceSystemObservationTool.ToolId);
                ValidateSample(system.SampleStartedAtUtc, system.SampleDuration);
                ValidatePercentage(system.CpuUtilizationPercent, "system CPU utilization");
                if (system.PhysicalMemoryAvailableBytes > system.PhysicalMemoryTotalBytes)
                {
                    throw new InvalidOperationException("Available physical memory cannot exceed total physical memory.");
                }

                if (system.MemoryLoadPercent is < 0 or > 100)
                {
                    throw new InvalidOperationException("Memory load must be between 0 and 100 percent.");
                }

                EnsureSqliteInteger(system.PhysicalMemoryTotalBytes, "total physical memory");
                EnsureSqliteInteger(system.PhysicalMemoryAvailableBytes, "available physical memory");
                return;
            case WindowsPerformanceTopProcesses processes:
                EnsureObservationTool(result, WindowsPerformanceTopProcessesObservationTool.ToolId);
                ValidateSample(processes.SampleStartedAtUtc, processes.SampleDuration);
                ValidateProcessEntries(processes.TopCpuProcesses, "cpu");
                ValidateProcessEntries(processes.TopMemoryProcesses, "memory");
                return;
            case WindowsRecentErrorEvents recentErrors:
                EnsureObservationTool(result, WindowsRecentErrorEventsObservationTool.ToolId);
                WindowsRecentErrorEventsValidation.Validate(recentErrors);
                return;
            default:
                throw new InvalidOperationException("The observation data type is not supported by persistence.");
        }
    }

    private static void EnsureObservationTool(ObservationResult result, string expectedToolId)
    {
        if (!string.Equals(result.ToolId, expectedToolId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The observation data type does not match the observation ToolId.");
        }
    }

    private static void ValidateSample(DateTimeOffset sampleStartedAtUtc, TimeSpan sampleDuration)
    {
        _ = sampleStartedAtUtc;
        if (sampleDuration <= TimeSpan.Zero || sampleDuration > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("The performance sample duration was outside the supported range.");
        }
    }

    private static void ValidateProcessEntries(
        IReadOnlyList<WindowsPerformanceProcess> processes,
        string rankingKind)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Count > 10)
        {
            throw new InvalidOperationException($"The {rankingKind} process ranking exceeds the supported bound.");
        }

        var processIds = new HashSet<int>();
        foreach (var process in processes)
        {
            ArgumentNullException.ThrowIfNull(process);
            if (process.ProcessId <= 0 || string.IsNullOrWhiteSpace(process.ProcessName) ||
                !processIds.Add(process.ProcessId))
            {
                throw new InvalidOperationException($"The {rankingKind} process ranking contains invalid identity data.");
            }

            ValidatePercentage(process.CpuUtilizationPercent, "process CPU utilization");
            EnsureSqliteInteger(process.WorkingSetBytes, "process working-set bytes");
        }
    }

    private static void ValidatePercentage(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100)
        {
            throw new InvalidOperationException($"The {name} value must be finite and between 0 and 100 percent.");
        }
    }

    private static long EnsureSqliteInteger(ulong value, string name)
    {
        if (value > long.MaxValue)
        {
            throw new InvalidOperationException($"The {name} value is too large for SQLite integer storage.");
        }

        return (long)value;
    }

    private static void InsertPerformanceSystemObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long observationId,
        WindowsPerformanceSystem system)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WindowsPerformanceSystemObservations
                (ObservationId, SampleStartedAtUtc, SampleDurationTicks, CpuUtilizationPercent,
                 PhysicalMemoryTotalBytes, PhysicalMemoryAvailableBytes, MemoryLoadPercent)
            VALUES ($observation, $started, $duration, $cpu, $total, $available, $load);
            """;
        SqliteDatabase.AddParameter(command, "$observation", observationId);
        SqliteDatabase.AddParameter(command, "$started", FormatDate(system.SampleStartedAtUtc));
        SqliteDatabase.AddParameter(command, "$duration", system.SampleDuration.Ticks);
        SqliteDatabase.AddParameter(command, "$cpu", system.CpuUtilizationPercent);
        SqliteDatabase.AddParameter(command, "$total", EnsureSqliteInteger(system.PhysicalMemoryTotalBytes, "total physical memory"));
        SqliteDatabase.AddParameter(command, "$available", EnsureSqliteInteger(system.PhysicalMemoryAvailableBytes, "available physical memory"));
        SqliteDatabase.AddParameter(command, "$load", system.MemoryLoadPercent);
        command.ExecuteNonQuery();
    }

    private static void InsertTopProcessObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long observationId,
        WindowsPerformanceTopProcesses processes)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO WindowsPerformanceTopProcessSnapshots
                    (ObservationId, SampleStartedAtUtc, SampleDurationTicks)
                VALUES ($observation, $started, $duration);
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            SqliteDatabase.AddParameter(command, "$started", FormatDate(processes.SampleStartedAtUtc));
            SqliteDatabase.AddParameter(command, "$duration", processes.SampleDuration.Ticks);
            command.ExecuteNonQuery();
        }

        InsertTopProcessEntries(connection, transaction, observationId, "cpu", processes.TopCpuProcesses);
        InsertTopProcessEntries(connection, transaction, observationId, "memory", processes.TopMemoryProcesses);
    }

    private static void InsertTopProcessEntries(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long observationId,
        string rankingKind,
        IReadOnlyList<WindowsPerformanceProcess> processes)
    {
        for (var index = 0; index < processes.Count; index++)
        {
            var process = processes[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO WindowsPerformanceTopProcessEntries
                    (ObservationId, RankingKind, RankingOrdinal, ProcessId, ProcessName,
                     CpuUtilizationPercent, WorkingSetBytes)
                VALUES ($observation, $ranking, $ordinal, $pid, $name, $cpu, $workingSet);
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            SqliteDatabase.AddParameter(command, "$ranking", rankingKind);
            SqliteDatabase.AddParameter(command, "$ordinal", index);
            SqliteDatabase.AddParameter(command, "$pid", process.ProcessId);
            SqliteDatabase.AddParameter(command, "$name", process.ProcessName);
            SqliteDatabase.AddParameter(command, "$cpu", process.CpuUtilizationPercent);
            SqliteDatabase.AddParameter(command, "$workingSet", EnsureSqliteInteger(process.WorkingSetBytes, "process working-set bytes"));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertRecentErrorEventsObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long observationId,
        WindowsRecentErrorEvents recentErrors)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO WindowsRecentErrorEventSnapshots
                    (ObservationId, WindowStartUtc, WindowEndUtc, IsTruncated)
                VALUES ($observation, $windowStart, $windowEnd, $truncated);
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            SqliteDatabase.AddParameter(command, "$windowStart", FormatDate(recentErrors.WindowStartUtc));
            SqliteDatabase.AddParameter(command, "$windowEnd", FormatDate(recentErrors.WindowEndUtc));
            SqliteDatabase.AddParameter(command, "$truncated", recentErrors.IsTruncated ? 1 : 0);
            command.ExecuteNonQuery();
        }

        for (var index = 0; index < recentErrors.Events.Count; index++)
        {
            var diagnosticEvent = recentErrors.Events[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO WindowsRecentErrorEventEntries
                    (ObservationId, Ordinal, ChannelKind, ProviderName, EventId, Severity, OccurredAtUtc)
                VALUES ($observation, $ordinal, $channel, $provider, $eventId, $severity, $occurred);
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            SqliteDatabase.AddParameter(command, "$ordinal", index);
            SqliteDatabase.AddParameter(command, "$channel", diagnosticEvent.Channel.ToString());
            SqliteDatabase.AddParameter(command, "$provider", diagnosticEvent.ProviderName);
            SqliteDatabase.AddParameter(command, "$eventId", diagnosticEvent.EventId);
            SqliteDatabase.AddParameter(command, "$severity", diagnosticEvent.Severity.ToString());
            SqliteDatabase.AddParameter(command, "$occurred", FormatDate(diagnosticEvent.OccurredAtUtc));
            command.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<InvestigationPlanHistoryEntry> ReadPlans(
        SqliteConnection connection,
        Guid investigationId)
    {
        var planHeaders = new List<(int Sequence, DateTimeOffset AcceptedAtUtc, string Objective)>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PlanSequence, AcceptedAtUtc, Objective
            FROM Plans WHERE InvestigationId = $id ORDER BY PlanSequence;
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                planHeaders.Add((
                    reader.GetInt32(0),
                    ParseDate(reader.GetString(1)),
                    reader.GetString(2)));
            }
        }

        return planHeaders
            .Select(header => new InvestigationPlanHistoryEntry(
                header.Sequence,
                header.AcceptedAtUtc,
                new InvestigationPlan(
                    header.Objective,
                    ReadPlanSteps(connection, investigationId, header.Sequence))))
            .ToArray();
    }

    private static IReadOnlyList<InvestigationStep> ReadPlanSteps(
        SqliteConnection connection,
        Guid investigationId,
        int planSequence)
    {
        var steps = new List<InvestigationStep>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StepId, ToolId, Status
            FROM PlanSteps
            WHERE InvestigationId = $id AND PlanSequence = $sequence
            ORDER BY Ordinal;
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$sequence", planSequence);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            steps.Add(new InvestigationStep(
                reader.GetString(0),
                reader.GetString(1),
                (InvestigationStepStatus)reader.GetInt32(2)));
        }

        return steps;
    }

    private static IReadOnlyList<InvestigationStepExecution> ReadExecutions(
        SqliteConnection connection,
        Guid investigationId)
    {
        var executions = new List<InvestigationStepExecution>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT se.PlanSequence, se.StepId, se.ToolId, se.RequestId,
                   se.RequestedAtUtc, se.ObservedAtUtc, se.ToolContractVersion, se.Status,
                   o.ObservationId, o.RequestId, o.ToolId, o.ObservedAtUtc, o.Status,
                   o.FailureCode, o.FailureMessage, o.Platform, o.OsVersion,
                   o.Build, o.Architecture
            FROM StepExecutions se
            LEFT JOIN Observations o ON se.ObservationId = o.ObservationId
            WHERE se.InvestigationId = $id
            ORDER BY se.PlanSequence, se.ExecutionId;
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ObservationResult? result = null;
            if (!reader.IsDBNull(8))
            {
                var observationId = reader.GetInt64(8);
                var data = reader.GetInt32(12) == (int)ObservationStatus.Succeeded
                    ? ReadObservationData(
                        connection,
                        observationId,
                        reader.GetString(10),
                        reader,
                        15,
                        16,
                        17,
                        18)
                    : null;
                var failure = reader.IsDBNull(13)
                    ? null
                    : new ObservationFailure(reader.GetString(13), reader.GetString(14));
                result = new ObservationResult(
                    ParseGuid(reader.GetString(9)),
                    reader.GetString(10),
                    ParseDate(reader.GetString(11)),
                    (ObservationStatus)reader.GetInt32(12),
                    data,
                    failure);
            }

            executions.Add(new InvestigationStepExecution(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseGuid(reader.GetString(3)),
                ParseDate(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
                reader.GetString(6),
                (InvestigationStepStatus)reader.GetInt32(7),
                result));
        }

        return executions;
    }

    private static IObservationData? ReadObservationData(
        SqliteConnection connection,
        long observationId,
        string toolId,
        SqliteDataReader observationReader,
        int platformIndex,
        int osVersionIndex,
        int buildIndex,
        int architectureIndex)
    {
        if (string.Equals(toolId, WindowsSystemInfoObservationTool.ToolId, StringComparison.Ordinal))
        {
            return observationReader.IsDBNull(platformIndex)
                ? null
                : new WindowsSystemInfo(
                    observationReader.GetString(platformIndex),
                    observationReader.GetString(osVersionIndex),
                    observationReader.IsDBNull(buildIndex) ? null : observationReader.GetInt32(buildIndex),
                    observationReader.GetString(architectureIndex));
        }

        if (string.Equals(toolId, WindowsPerformanceSystemObservationTool.ToolId, StringComparison.Ordinal))
        {
            return ReadPerformanceSystemObservation(connection, observationId);
        }

        if (string.Equals(toolId, WindowsPerformanceTopProcessesObservationTool.ToolId, StringComparison.Ordinal))
        {
            return ReadTopProcessObservation(connection, observationId);
        }

        if (string.Equals(toolId, WindowsRecentErrorEventsObservationTool.ToolId, StringComparison.Ordinal))
        {
            return ReadRecentErrorEventsObservation(connection, observationId);
        }

        return null;
    }

    private static WindowsPerformanceSystem ReadPerformanceSystemObservation(
        SqliteConnection connection,
        long observationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SampleStartedAtUtc, SampleDurationTicks, CpuUtilizationPercent,
                   PhysicalMemoryTotalBytes, PhysicalMemoryAvailableBytes, MemoryLoadPercent
            FROM WindowsPerformanceSystemObservations
            WHERE ObservationId = $observation;
            """;
        SqliteDatabase.AddParameter(command, "$observation", observationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvestigationPersistenceException(
                "The persisted system performance observation is incomplete.");
        }

        var duration = ReadSampleDuration(reader.GetInt64(1));
        var cpu = reader.GetDouble(2);
        ValidatePercentage(cpu, "system CPU utilization");
        var total = ReadUnsignedInteger(reader.GetInt64(3), "total physical memory");
        var available = ReadUnsignedInteger(reader.GetInt64(4), "available physical memory");
        var load = reader.GetInt32(5);
        if (available > total || load is < 0 or > 100)
        {
            throw new InvestigationPersistenceException(
                "The persisted system performance observation contains invalid memory data.");
        }

        return new WindowsPerformanceSystem(
            cpu,
            total,
            available,
            load,
            ParseDate(reader.GetString(0)),
            duration);
    }

    private static WindowsPerformanceTopProcesses ReadTopProcessObservation(
        SqliteConnection connection,
        long observationId)
    {
        DateTimeOffset sampleStartedAtUtc;
        TimeSpan sampleDuration;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT SampleStartedAtUtc, SampleDurationTicks
                FROM WindowsPerformanceTopProcessSnapshots
                WHERE ObservationId = $observation;
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvestigationPersistenceException(
                    "The persisted top-process observation is incomplete.");
            }

            sampleStartedAtUtc = ParseDate(reader.GetString(0));
            sampleDuration = ReadSampleDuration(reader.GetInt64(1));
        }

        var cpuProcesses = ReadTopProcessEntries(connection, observationId, "cpu");
        var memoryProcesses = ReadTopProcessEntries(connection, observationId, "memory");
        return new WindowsPerformanceTopProcesses(
            sampleStartedAtUtc,
            sampleDuration,
            cpuProcesses,
            memoryProcesses);
    }

    private static IReadOnlyList<WindowsPerformanceProcess> ReadTopProcessEntries(
        SqliteConnection connection,
        long observationId,
        string rankingKind)
    {
        var processes = new List<WindowsPerformanceProcess>();
        var processIds = new HashSet<int>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RankingOrdinal, ProcessId, ProcessName, CpuUtilizationPercent, WorkingSetBytes
            FROM WindowsPerformanceTopProcessEntries
            WHERE ObservationId = $observation AND RankingKind = $ranking
            ORDER BY RankingOrdinal;
            """;
        SqliteDatabase.AddParameter(command, "$observation", observationId);
        SqliteDatabase.AddParameter(command, "$ranking", rankingKind);
        using var reader = command.ExecuteReader();
        var expectedOrdinal = 0;
        while (reader.Read())
        {
            if (reader.GetInt32(0) != expectedOrdinal)
            {
                throw new InvestigationPersistenceException(
                    "The persisted top-process ranking has an invalid ordinal.");
            }

            var processId = reader.GetInt32(1);
            var processName = reader.GetString(2);
            var cpu = reader.GetDouble(3);
            var workingSet = ReadUnsignedInteger(reader.GetInt64(4), "process working-set bytes");
            if (processId <= 0 || string.IsNullOrWhiteSpace(processName) || !processIds.Add(processId))
            {
                throw new InvestigationPersistenceException(
                    "The persisted top-process ranking contains invalid identity data.");
            }

            ValidatePercentage(cpu, "process CPU utilization");
            processes.Add(new WindowsPerformanceProcess(processId, processName, cpu, workingSet));
            expectedOrdinal++;
        }

        if (processes.Count > 10)
        {
            throw new InvestigationPersistenceException(
                "The persisted top-process ranking exceeds the supported bound.");
        }

        return processes;
    }

    private static WindowsRecentErrorEvents ReadRecentErrorEventsObservation(
        SqliteConnection connection,
        long observationId)
    {
        DateTimeOffset windowStartUtc;
        DateTimeOffset windowEndUtc;
        bool isTruncated;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT WindowStartUtc, WindowEndUtc, IsTruncated
                FROM WindowsRecentErrorEventSnapshots
                WHERE ObservationId = $observation;
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvestigationPersistenceException(
                    "The persisted recent event observation is incomplete.");
            }

            windowStartUtc = ParseDate(reader.GetString(0));
            windowEndUtc = ParseDate(reader.GetString(1));
            var truncated = reader.GetInt32(2);
            if (truncated is not (0 or 1))
            {
                throw new InvestigationPersistenceException(
                    "The persisted recent event observation has an invalid truncation value.");
            }

            isTruncated = truncated == 1;
        }

        var events = new List<WindowsDiagnosticEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Ordinal, ChannelKind, ProviderName, EventId, Severity, OccurredAtUtc
                FROM WindowsRecentErrorEventEntries
                WHERE ObservationId = $observation
                ORDER BY Ordinal;
                """;
            SqliteDatabase.AddParameter(command, "$observation", observationId);
            using var reader = command.ExecuteReader();
            var expectedOrdinal = 0;
            while (reader.Read())
            {
                if (reader.GetInt32(0) != expectedOrdinal)
                {
                    throw new InvestigationPersistenceException(
                        "The persisted recent event observation has an invalid ordinal.");
                }

                if (expectedOrdinal >= WindowsRecentErrorEventsValidation.MaximumEvents)
                {
                    throw new InvestigationPersistenceException(
                        "The persisted recent event observation exceeds its total event bound.");
                }

                var channel = reader.GetString(1) switch
                {
                    "System" => WindowsEventChannelKind.System,
                    "Application" => WindowsEventChannelKind.Application,
                    _ => throw new InvestigationPersistenceException(
                        "The persisted recent event observation contains an invalid channel.")
                };
                var severity = reader.GetString(4) switch
                {
                    "Critical" => WindowsEventSeverity.Critical,
                    "Error" => WindowsEventSeverity.Error,
                    _ => throw new InvestigationPersistenceException(
                        "The persisted recent event observation contains an invalid severity.")
                };

                if (channel is not (WindowsEventChannelKind.System or WindowsEventChannelKind.Application) ||
                    severity is not (WindowsEventSeverity.Critical or WindowsEventSeverity.Error))
                {
                    throw new InvestigationPersistenceException(
                        "The persisted recent event observation contains an invalid closed value.");
                }

                events.Add(new WindowsDiagnosticEvent(
                    ParseDate(reader.GetString(5)),
                    channel,
                    reader.GetString(2),
                    reader.GetInt32(3),
                    severity));
                expectedOrdinal++;
            }
        }

        var data = new WindowsRecentErrorEvents(
            windowStartUtc,
            windowEndUtc,
            isTruncated,
            events);
        try
        {
            WindowsRecentErrorEventsValidation.Validate(data);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new InvestigationPersistenceException(
                "The persisted recent event observation is invalid.",
                exception);
        }

        return data;
    }

    private static TimeSpan ReadSampleDuration(long ticks)
    {
        if (ticks <= 0)
        {
            throw new InvestigationPersistenceException("The persisted performance sample duration is invalid.");
        }

        var duration = TimeSpan.FromTicks(ticks);
        if (duration > TimeSpan.FromSeconds(5))
        {
            throw new InvestigationPersistenceException("The persisted performance sample duration is too large.");
        }

        return duration;
    }

    private static ulong ReadUnsignedInteger(long value, string name)
    {
        if (value < 0)
        {
            throw new InvestigationPersistenceException($"The persisted {name} value is negative.");
        }

        return (ulong)value;
    }

    private static InvestigationOutcome? CreateOutcome(
        SqliteDataReader reader,
        int answerIndex,
        int failureCodeIndex,
        int failureMessageIndex)
    {
        if (reader.IsDBNull(answerIndex) &&
            reader.IsDBNull(failureCodeIndex) &&
            reader.IsDBNull(failureMessageIndex))
        {
            return null;
        }

        return new InvestigationOutcome(
            reader.IsDBNull(answerIndex) ? null : reader.GetString(answerIndex),
            reader.IsDBNull(failureCodeIndex) ? null : reader.GetString(failureCodeIndex),
            reader.IsDBNull(failureMessageIndex) ? null : reader.GetString(failureMessageIndex));
    }

    private static void ValidateExecutionResult(InvestigationStepExecution execution)
    {
        if (execution.Result is null)
        {
            if (execution.Status is not (InvestigationStepStatus.Failed or InvestigationStepStatus.Cancelled))
            {
                throw new InvalidOperationException(
                    "Only failed or cancelled executions may omit an observation result.");
            }

            return;
        }

        var expectedStatus = execution.Result.Status switch
        {
            ObservationStatus.Succeeded => InvestigationStepStatus.Completed,
            ObservationStatus.Failed => InvestigationStepStatus.Failed,
            _ => throw new InvalidOperationException("The observation result has an unsupported status.")
        };
        if (execution.Status != expectedStatus)
        {
            throw new InvalidOperationException("The execution status does not match the observation result status.");
        }
    }

    private static void EnsureInvestigationRunning(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT LifecycleStatus
            FROM Investigations
            WHERE InvestigationId = $id;
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        var value = command.ExecuteScalar();
        if (value is null || Convert.ToInt32(value, CultureInfo.InvariantCulture) != (int)InvestigationLifecycleStatus.Running)
        {
            throw new InvestigationPersistenceException(
                "Investigation history can only be modified while the investigation is running.");
        }
    }

    private static (string ToolId, InvestigationStepStatus Status) ReadPlanStep(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid investigationId,
        InvestigationStepExecution execution)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ToolId, Status
            FROM PlanSteps
            WHERE InvestigationId = $id AND PlanSequence = $sequence AND StepId = $step;
            """;
        SqliteDatabase.AddParameter(command, "$id", investigationId.ToString("D"));
        SqliteDatabase.AddParameter(command, "$sequence", execution.PlanSequence);
        SqliteDatabase.AddParameter(command, "$step", execution.StepId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvestigationPersistenceException("The referenced investigation step could not be found.");
        }

        return (reader.GetString(0), (InvestigationStepStatus)reader.GetInt32(1));
    }

    private void EnsureInitialized() => _database.Initialize();

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture);

    private static string? FormatDateOrNull(DateTimeOffset? value) =>
        value is null ? null : FormatDate(value.Value);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");
}
