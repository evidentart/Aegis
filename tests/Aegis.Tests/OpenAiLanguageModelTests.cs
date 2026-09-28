using Aegis;
using Aegis.Core;
using System.ClientModel;
using System.Text.Json;
using Xunit;

namespace Aegis.Tests;

public sealed class OpenAiLanguageModelTests
{
    [Fact]
    public async Task MapsProviderNeutralMessagesAndConvertsFinalAnswer()
    {
        var client = new FakeOpenAiChatClient("{\"kind\":\"final_answer\",\"summary\":\"The answer.\",\"observed_facts\":[],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[],\"recommendations\":[]}");
        var model = new OpenAiLanguageModel(client);

        var response = await model.CompleteAsync(CreateRequest(
            new LanguageModelMessage(LanguageModelMessageRole.System, "System message."),
            new LanguageModelMessage(LanguageModelMessageRole.User, "Question."),
            new LanguageModelMessage(LanguageModelMessageRole.Assistant, "Prior decision."),
            new LanguageModelMessage(LanguageModelMessageRole.Observation, "Evidence.")));

        var finalDecision = Assert.IsType<FinalAnswerDecision>(response);
        Assert.Equal("The answer.", finalDecision.Report.Summary);
        Assert.Empty(finalDecision.Report.ObservedFacts);
        Assert.Empty(finalDecision.Report.Conclusions);
        Assert.Empty(finalDecision.Report.Hypotheses);
        Assert.Empty(finalDecision.Report.Uncertainties);
        Assert.Empty(finalDecision.Report.Recommendations);
        Assert.Collection(
            client.Requests.Single().Messages,
            message =>
            {
                Assert.Equal(LanguageModelMessageRole.System, message.Role);
                Assert.Equal("System message.", message.Content);
            },
            message =>
            {
                Assert.Equal(LanguageModelMessageRole.User, message.Role);
                Assert.Equal("Question.", message.Content);
            },
            message =>
            {
                Assert.Equal(LanguageModelMessageRole.Assistant, message.Role);
                Assert.Equal("Prior decision.", message.Content);
            },
            message =>
            {
                Assert.Equal(LanguageModelMessageRole.Observation, message.Role);
                Assert.Equal("Evidence.", message.Content);
            });
    }

    [Fact]
    public async Task ParsesStructuredFinalAnswerWithEvidenceReferences()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            "{\"kind\":\"final_answer\",\"summary\":\"Summary\",\"observed_facts\":[{\"text\":\"CPU was observed\",\"evidence_step_ids\":[\"perf-system-1\"]}],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[\"The sample was bounded.\"],\"recommendations\":[\"Collect another sample if needed.\"]}"));

        var decision = Assert.IsType<FinalAnswerDecision>(await model.CompleteAsync(
            CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("Summary", decision.Report.Summary);
        var fact = Assert.Single(decision.Report.ObservedFacts);
        Assert.Equal("CPU was observed", fact.Text);
        Assert.Equal("perf-system-1", Assert.Single(fact.EvidenceStepIds));
        Assert.Equal("The sample was bounded.", Assert.Single(decision.Report.Uncertainties));
    }

    [Fact]
    public async Task RejectsStructuredFinalAnswerWithUnsupportedProperty()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            "{\"kind\":\"final_answer\",\"summary\":\"Summary\",\"observed_facts\":[],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[],\"recommendations\":[],\"extra\":\"not allowed\"}"));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model returned an invalid decision.", exception.Message);
    }

    [Fact]
    public async Task RejectsStructuredFinalAnswerMissingRequiredReportField()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            "{\"kind\":\"final_answer\",\"summary\":\"Summary\",\"observed_facts\":[],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[]}"));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model returned an invalid decision.", exception.Message);
    }

    [Fact]
    public async Task ConvertsInvestigationPlanDecision()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            "{\"kind\":\"investigation_plan\",\"objective\":\"Inspect the system.\",\"steps\":[{\"step_id\":\"step-1\",\"tool_id\":\"windows.system.info\"}]}"));

        var decision = await model.CompleteAsync(CreateRequest(
            new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")));

        var planDecision = Assert.IsType<InvestigationPlanDecision>(decision);
        Assert.Equal("Inspect the system.", planDecision.Plan.Objective);
        var step = Assert.Single(planDecision.Plan.Steps);
        Assert.Equal("step-1", step.StepId);
        Assert.Equal("windows.system.info", step.ToolId);
        Assert.Equal(InvestigationStepStatus.Pending, step.Status);
    }

    [Fact]
    public async Task AppliesPhaseSpecificOutputBoundsAndStructuredSchemas()
    {
        var client = new FakeOpenAiChatClient(
            "{\"kind\":\"final_answer\",\"summary\":\"Summary\",\"observed_facts\":[],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[],\"recommendations\":[]}");
        var model = new OpenAiLanguageModel(client);

        await model.CompleteAsync(new LanguageModelRequest(
            [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
            LanguageModelCallPhase.Finalization));

        var request = Assert.Single(client.Requests);
        Assert.Equal(OpenAiResponseSchemas.MaximumFinalizationOutputTokens, request.MaxOutputTokenCount);
        Assert.Equal(OpenAiReasoningEffort.Minimal, request.ReasoningEffort);
        Assert.Contains("final_answer", request.ResponseSchema);
        Assert.Contains("additionalProperties", request.ResponseSchema);
        using var schema = JsonDocument.Parse(request.ResponseSchema);
        Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            1,
            schema.RootElement
                .GetProperty("properties")
                .EnumerateObject()
                .Count(property => property.Name == "conclusions"));
        Assert.Equal(
            1,
            schema.RootElement
                .GetProperty("required")
                .EnumerateArray()
                .Count(property => property.GetString() == "conclusions"));

        var planningClient = new FakeOpenAiChatClient(
            "{\"kind\":\"investigation_plan\",\"objective\":\"Inspect.\",\"steps\":[{\"step_id\":\"step-1\",\"tool_id\":\"tool.one\"}]}");
        var planningModel = new OpenAiLanguageModel(planningClient);
        await planningModel.CompleteAsync(new LanguageModelRequest(
            [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
            LanguageModelCallPhase.InitialPlanning));

        var planningRequest = Assert.Single(planningClient.Requests);
        Assert.Equal(OpenAiResponseSchemas.MaximumPlanningOutputTokens, planningRequest.MaxOutputTokenCount);
        Assert.Equal(OpenAiReasoningEffort.Minimal, planningRequest.ReasoningEffort);
        Assert.True(
            planningRequest.MaxOutputTokenCount > 512,
            "Planning must retain more output headroom than the previous 512-token bound.");
        using var planningSchema = JsonDocument.Parse(planningRequest.ResponseSchema);
        Assert.Equal(JsonValueKind.Object, planningSchema.RootElement.ValueKind);

        var replanningClient = new FakeOpenAiChatClient(
            "{\"kind\":\"investigation_plan\",\"objective\":\"Inspect.\",\"steps\":[{\"step_id\":\"step-1\",\"tool_id\":\"tool.one\"}]}");
        var replanningModel = new OpenAiLanguageModel(replanningClient);
        await replanningModel.CompleteAsync(new LanguageModelRequest(
            [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
            LanguageModelCallPhase.Replanning));

        var replanningRequest = Assert.Single(replanningClient.Requests);
        Assert.Equal(OpenAiResponseSchemas.MaximumPlanningOutputTokens, replanningRequest.MaxOutputTokenCount);
        Assert.Equal(OpenAiReasoningEffort.Minimal, replanningRequest.ReasoningEffort);
    }

    [Fact]
    public async Task MissingUsageMetadataDoesNotBreakSuccessfulDecision()
    {
        LanguageModelCallDiagnostics? diagnostics = null;
        var model = new OpenAiLanguageModel(
            new FakeOpenAiChatClient(
                "{\"kind\":\"final_answer\",\"summary\":\"Summary\",\"observed_facts\":[],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[],\"recommendations\":[]}"),
            "gpt-test",
            value => diagnostics = value);

        await model.CompleteAsync(new LanguageModelRequest(
            [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
            LanguageModelCallPhase.Finalization));

        Assert.NotNull(diagnostics);
        Assert.Equal(LanguageModelCallOutcome.Succeeded, diagnostics!.Outcome);
        Assert.Equal(LanguageModelCallPhase.Finalization, diagnostics.Phase);
        Assert.Equal("gpt-test", diagnostics.Model);
        Assert.Null(diagnostics.InputTokenCount);
        Assert.Null(diagnostics.OutputTokenCount);
        Assert.Null(diagnostics.TotalTokenCount);
    }

    [Fact]
    public async Task ProviderFailureWithoutResponseIsClassifiedSafely()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            exception: new ClientResultException("provider rejected", null, null)));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(new LanguageModelRequest(
                [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
                LanguageModelCallPhase.Finalization)));

        Assert.Equal(LanguageModelFailureCategory.ProviderUnavailable, exception.Category);
    }

    [Fact]
    public async Task ProviderTimeoutIsClassifiedAsUnavailable()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            exception: new TimeoutException("timed out")));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(new LanguageModelRequest(
                [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
                LanguageModelCallPhase.InitialPlanning)));

        Assert.Equal(LanguageModelFailureCategory.ProviderUnavailable, exception.Category);
    }

    [Fact]
    public async Task CancellationIsReportedWithoutReplacingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        LanguageModelCallDiagnostics? diagnostics = null;
        var model = new OpenAiLanguageModel(
            new FakeOpenAiChatClient(cancellationToken: cancellation.Token),
            reportDiagnostics: value => diagnostics = value);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            model.CompleteAsync(
                new LanguageModelRequest(
                    [new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")],
                    LanguageModelCallPhase.InitialPlanning),
                cancellation.Token));

        Assert.NotNull(diagnostics);
        Assert.Equal(LanguageModelFailureCategory.Cancelled, diagnostics!.FailureCategory);
    }

    [Fact]
    public void BadRequestDuringStructuredCallRemainsProviderRejectionWithoutReliableSchemaEvidence()
    {
        Assert.Equal(
            LanguageModelFailureCategory.ProviderRejected,
            OpenAiLanguageModel.ClassifyProviderFailure(400));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void AuthenticationProviderFailuresAreClassifiedSafely(int status)
    {
        Assert.Equal(
            LanguageModelFailureCategory.AuthenticationRejected,
            OpenAiLanguageModel.ClassifyProviderFailure(status));
    }

    [Fact]
    public async Task RejectsEmptyInvestigationPlan()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            "{\"kind\":\"investigation_plan\",\"objective\":\"Inspect the system.\",\"steps\":[]}"));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model returned an invalid decision.", exception.Message);
    }

    [Fact]
    public async Task RejectsStepStatusAndArbitraryArguments()
    {
        var contents = new[]
        {
            "{\"kind\":\"investigation_plan\",\"objective\":\"Inspect.\",\"steps\":[{\"step_id\":\"step-1\",\"tool_id\":\"tool.one\",\"status\":\"Completed\"}]}",
            "{\"kind\":\"investigation_plan\",\"objective\":\"Inspect.\",\"steps\":[{\"step_id\":\"step-1\",\"tool_id\":\"tool.one\",\"arguments\":{}}]}"
        };

        foreach (var content in contents)
        {
            var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(content));

            var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
                model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

            Assert.Equal("The language model returned an invalid decision.", exception.Message);
        }
    }

    [Fact]
    public async Task RejectsObsoleteObservationRequestDecision()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            "{\"kind\":\"observation_request\",\"tool_id\":\"windows.system.info\"}"));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model returned an invalid decision.", exception.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[{}]")]
    [InlineData("{\"kind\":\"unknown\",\"answer\":\"text\"}")]
    [InlineData("{\"kind\":\"final_answer\",\"answer\":\"text\"}")]
    [InlineData("{\"kind\":\"final_answer\",\"answer\":\"text\",\"arguments\":{}}")]
    [InlineData("{\"kind\":\"final_answer\",\"summary\":\"text\",\"observed_facts\":[{\"text\":\"fact\",\"evidence_step_ids\":[]}],\"conclusions\":[],\"hypotheses\":[],\"uncertainties\":[],\"recommendations\":[]}")]
    [InlineData("```json\n{\"kind\":\"final_answer\",\"answer\":\"text\"}\n```")]
    public async Task RejectsMalformedProviderDecisions(string content)
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(content));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model returned an invalid decision.", exception.Message);
    }

    [Theory]
    [InlineData("{\"kind\":\"final_answer\",\"answer\":\"one\",\"answer\":\"two\"}")]
    [InlineData("{\"kind\":\"investigation_plan\",\"objective\":\"Inspect.\",\"objective\":\"Again.\",\"steps\":[{\"step_id\":\"step-1\",\"tool_id\":\"tool.one\"}]}")]
    [InlineData("{\"kind\":\"investigation_plan\",\"objective\":\"Inspect.\",\"steps\":[{\"step_id\":\"step-1\",\"step_id\":\"step-2\",\"tool_id\":\"tool.one\"}]}")]
    public async Task RejectsDuplicateProviderDecisionProperties(string content)
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(content));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model returned an invalid decision.", exception.Message);
    }

    [Fact]
    public async Task PreservesProviderFailure()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            exception: new InvalidOperationException("provider failure")));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            model.CompleteAsync(CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question."))));

        Assert.Equal("The language model request failed.", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(
            cancellationToken: cancellation.Token));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            model.CompleteAsync(
                CreateRequest(new LanguageModelMessage(LanguageModelMessageRole.User, "Question.")),
                cancellation.Token));
    }

    private static LanguageModelRequest CreateRequest(params LanguageModelMessage[] messages) =>
        new LanguageModelRequest(messages);

    private sealed class FakeOpenAiChatClient : IOpenAiChatClient
    {
        private readonly string? _answer;
        private readonly Exception? _exception;
        private readonly CancellationToken? _cancellationToken;

        public FakeOpenAiChatClient(
            string? answer = null,
            Exception? exception = null,
            CancellationToken? cancellationToken = null)
        {
            _answer = answer;
            _exception = exception;
            _cancellationToken = cancellationToken;
        }

        public List<OpenAiCompletionRequest> Requests { get; } = [];

        public Task<OpenAiCompletion> CompleteAsync(
            OpenAiCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_exception is not null)
            {
                throw _exception;
            }

            if (_cancellationToken is { } token)
            {
                return Task.FromCanceled<OpenAiCompletion>(token);
            }

            return Task.FromResult(new OpenAiCompletion(
                _answer ?? string.Empty,
                "stop",
                InputTokenCount: null,
                OutputTokenCount: null,
                TotalTokenCount: null));
        }
    }
}
