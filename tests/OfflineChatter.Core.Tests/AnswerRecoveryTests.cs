using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class AnswerRecoveryTests
{
    private static readonly string[] Tools = { "calculator", "fetch_url" };

    [Fact]
    public void Answer_Stuck_In_Reasoning_After_A_Tool_Becomes_The_Answer()
    {
        // Qwen 3.5: шаблон открыл <think>, модель ответила сразу — сервер отдал всё как reasoning_content
        var a = AnswerRecovery.Finish("The result is 42.", "", null, "stop", Tools);
        Assert.True(a.Promoted);
        Assert.Equal("The result is 42.", a.Content);
        Assert.Equal(string.Empty, a.Reasoning);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("cancelled")]
    public void Unfinished_Reasoning_Is_Not_Presented_As_An_Answer(string finish)
    {
        var a = AnswerRecovery.Finish("Let me think about", "", null, finish, Tools);
        Assert.False(a.Promoted);
        Assert.Equal(string.Empty, a.Content);
        Assert.Equal("Let me think about", a.Reasoning);
    }

    [Fact]
    public void Normal_Answer_Is_Left_Alone()
    {
        var a = AnswerRecovery.Finish("plan", "Answer", null, "stop", Tools);
        Assert.False(a.Promoted);
        Assert.Equal("Answer", a.Content);
        Assert.Equal("plan", a.Reasoning);
    }

    [Fact]
    public void Tool_Call_Step_Is_Not_Promoted()
    {
        var calls = new List<ToolCall> { new() { Id = "1", Name = "calculator", Arguments = "{}" } };
        var a = AnswerRecovery.Finish("I need the calculator", "", calls, "tool_calls", Tools);
        Assert.False(a.Promoted);
        Assert.Single(a.ToolCalls);
        Assert.Equal("I need the calculator", a.Reasoning);
    }

    [Theory]
    // Закрывающий тег без открывающего: открывающий остался в промпте
    [InlineData("", "thoughts here</think>\n\nAnswer", "thoughts here", "Answer")]
    // Сервер начал отдавать рассуждения, а закрывающий тег попал в текст
    [InlineData("first", "second</think>Answer", "first\nsecond", "Answer")]
    // Закрывающий тег остался внутри рассуждений
    [InlineData("thoughts</think>\nAnswer", "", "thoughts", "Answer")]
    // Обычная обёртка в тексте
    [InlineData("", "<think>plan</think>Answer", "plan", "Answer")]
    // Тег в середине обычного ответа — не трогаем
    [InlineData("", "Use <think>x</think> tags", "", "Use <think>x</think> tags")]
    public void Separates_Reasoning_From_Text(string reasoning, string content, string expectedReasoning, string expectedContent)
    {
        var (r, c) = AnswerRecovery.Separate(reasoning, content);
        Assert.Equal(expectedReasoning, r);
        Assert.Equal(expectedContent, c);
    }

    [Fact]
    public void Tool_Call_Left_As_Text_Inside_Reasoning_Is_Executed()
    {
        var reasoning = "I should compute it.\n<tool_call>\n{\"name\": \"calculator\", \"arguments\": {\"expression\": \"2+2\", \"opts\": {\"x\": 1}}}\n</tool_call>";
        var a = AnswerRecovery.Finish(reasoning, "", null, "stop", Tools);
        var call = Assert.Single(a.ToolCalls);
        Assert.True(a.ToolCallsFromText);
        Assert.False(a.Promoted);
        Assert.Equal("calculator", call.Name);
        Assert.Equal("2+2", (string?)JObject.Parse(call.Arguments)["expression"]);
        Assert.Equal("I should compute it.", a.Reasoning);
        Assert.StartsWith("call_", call.Id);
    }

    [Fact]
    public void Unknown_Tool_Or_No_Tools_Offered_Leaves_Text_Untouched()
    {
        const string text = "<tool_call>{\"name\": \"rm_rf\", \"arguments\": {}}</tool_call>";
        Assert.Empty(AnswerRecovery.Finish("", text, null, "stop", Tools).ToolCalls);
        var plain = AnswerRecovery.Finish("", "<tool_call>{\"name\": \"calculator\", \"arguments\": {}}</tool_call>", null, "stop", null);
        Assert.Empty(plain.ToolCalls);
        Assert.Contains("<tool_call>", plain.Content);
    }

    [Fact]
    public void Token_Estimate_Is_In_A_Sane_Range()
    {
        Assert.Equal(0, TokenEstimator.Estimate(""));
        Assert.InRange(TokenEstimator.Estimate("The quick brown fox jumps over the lazy dog."), 8, 14);
        Assert.InRange(TokenEstimator.Estimate("Съешь же ещё этих мягких французских булок, да выпей чаю."), 14, 30);
        Assert.InRange(TokenEstimator.Estimate("你好，世界"), 4, 6);
    }

    [Fact]
    public void Inspector_Layout_Defaults_Roundtrip_And_Unknown_Names()
    {
        var defaults = InspectorLayout.Resolve(null);
        Assert.Contains(InspectorSection.Connection, defaults);
        Assert.Contains(InspectorSection.Tokens, defaults);
        Assert.DoesNotContain(InspectorSection.Model, defaults);
        Assert.DoesNotContain(InspectorSection.Launch, defaults);

        // Пустой список — пользователь убрал всё; это не «по умолчанию»
        Assert.Empty(InspectorLayout.Resolve(new List<string>()));

        var set = InspectorLayout.Resolve(new[] { "tokens", "Model", "FromNewerVersion", "999" });
        Assert.Equal(new[] { "Tokens", "Model" }, InspectorLayout.Store(set));
    }
}
