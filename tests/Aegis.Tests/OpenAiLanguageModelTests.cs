using Aegis;
using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class OpenAiLanguageModelTests
{
    [Fact]
    public async Task MapsProviderNeutralMessagesAndConvertsFinalAnswer()
    {
        var client = new FakeOpenAiChatClient("{\"kind\":\"final_answer\",\"answer\":\"The answer.\"}");
        var model = new OpenAiLanguageModel(client);

        var response = await model.CompleteAsync(CreateRequest(
            new LanguageModelMessage(LanguageModelMessageRole.System, "System message."),
            new LanguageModelMessage(LanguageModelMessageRole.User, "Question."),
            new LanguageModelMessage(LanguageModelMessageRole.Assistant, "Prior decision."),
            new LanguageModelMessage(LanguageModelMessageRole.Observation, "Evidence.")));

        Assert.Equal(new FinalAnswerDecision("The answer."), response);
        Assert.Collection(
            client.Messages,
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
    [InlineData("{\"kind\":\"final_answer\",\"answer\":\"text\",\"arguments\":{}}")]
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

        public List<OpenAiMessage> Messages { get; } = [];

        public Task<string> CompleteAsync(
            IReadOnlyList<OpenAiMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            if (_exception is not null)
            {
                throw _exception;
            }

            if (_cancellationToken is { } token)
            {
                return Task.FromCanceled<string>(token);
            }

            return Task.FromResult(_answer ?? string.Empty);
        }
    }
}
