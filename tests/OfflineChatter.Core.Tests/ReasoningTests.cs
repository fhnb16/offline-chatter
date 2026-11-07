using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class ReasoningTests
{
    [Theory]
    // По шаблону чата — надёжнее всего
    [InlineData("anything", "{%- if enable_thinking is defined and enable_thinking is false %}", true, ReasoningStyle.TemplateFlag)]
    [InlineData("anything", "{{ reasoning_effort | default('medium') }}", true, ReasoningStyle.GptOss)]
    [InlineData("anything", "{%- if thinking %} You are Granite", true, ReasoningStyle.Granite)]
    [InlineData("anything", "{% if '/no_think' in system %}", true, ReasoningStyle.PromptSwitch)]
    [InlineData("anything", "{{ '<|Assistant|><think>\\n' }}", true, ReasoningStyle.AlwaysOn)]
    // По названию модели (локально, без шаблона)
    [InlineData("Qwen3-8B-Q4_K_M", null, true, ReasoningStyle.TemplateFlag)]
    [InlineData("Qwen3.5-0.8B-Weight-Space-Programming-Q4_K_M", null, true, ReasoningStyle.TemplateFlag)]
    [InlineData("gpt-oss-20b-mxfp4", null, true, ReasoningStyle.GptOss)]
    [InlineData("DeepSeek-R1-Distill-Qwen-7B", null, true, ReasoningStyle.AlwaysOn)]
    [InlineData("QwQ-32B", null, true, ReasoningStyle.AlwaysOn)]
    [InlineData("gemma-3n-E4B-it-Q8_0", null, true, ReasoningStyle.None)]
    [InlineData("granite-3.3-8b-instruct", null, true, ReasoningStyle.Granite)]
    [InlineData("SmolLM3-3B", null, true, ReasoningStyle.PromptSwitch)]
    // Облачные API
    [InlineData("gemini-2.5-flash", null, false, ReasoningStyle.ReasoningEffort)]
    [InlineData("o4-mini", null, false, ReasoningStyle.ReasoningEffort)]
    [InlineData("gpt-5-mini", null, false, ReasoningStyle.ReasoningEffort)]
    [InlineData("gpt-4o", null, false, ReasoningStyle.None)]
    public void Detects_Style(string model, string? template, bool llama, ReasoningStyle expected) =>
        Assert.Equal(expected, Reasoning.Detect(model, template, llama));

    [Fact]
    public void Gemini_Detected_By_Provider_Url_Even_For_Gemma()
    {
        // Gemma через API Gemini — тоже reasoning_effort? Нет: Gemma не размышляет, имя важнее адреса
        Assert.Equal(ReasoningStyle.ReasoningEffort, Reasoning.Detect("some-model", null, false, "https://generativelanguage.googleapis.com/v1beta/openai"));
    }

    private static JObject Body() => new() { ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "hi" }) };

    [Fact]
    public void Template_Flag_And_Auto_Level()
    {
        var b = Body();
        Reasoning.Apply(b, ReasoningStyle.TemplateFlag, ReasoningLevel.Off, "qwen3", null, true, null);
        Assert.False((bool)b["chat_template_kwargs"]!["enable_thinking"]!);

        var auto = Body();
        Reasoning.Apply(auto, ReasoningStyle.TemplateFlag, ReasoningLevel.Auto, "qwen3", null, true, null);
        Assert.Null(auto["chat_template_kwargs"]); // «как решит модель» — ничего не отправляем
    }

    [Fact]
    public void GptOss_Uses_Effort_Kwarg_Locally_And_Field_Remotely()
    {
        var local = Body();
        Reasoning.Apply(local, ReasoningStyle.Auto, ReasoningLevel.High, "gpt-oss-20b", null, true, null);
        Assert.Equal("high", (string?)local["chat_template_kwargs"]!["reasoning_effort"]);

        var remote = Body();
        Reasoning.Apply(remote, ReasoningStyle.Auto, ReasoningLevel.Low, "openai/gpt-oss-120b", null, false, "https://openrouter.ai/api/v1");
        Assert.Equal("low", (string?)remote["reasoning_effort"]);
    }

    [Fact]
    public void Gemini_Off_Means_None_OpenAi_Off_Means_Low()
    {
        var g = Body();
        Reasoning.Apply(g, ReasoningStyle.Auto, ReasoningLevel.Off, "gemini-2.5-flash", null, false, "https://generativelanguage.googleapis.com/v1beta/openai");
        Assert.Equal("none", (string?)g["reasoning_effort"]);

        var o = Body();
        Reasoning.Apply(o, ReasoningStyle.Auto, ReasoningLevel.Off, "o4-mini", null, false, "https://api.openai.com/v1");
        Assert.Equal("low", (string?)o["reasoning_effort"]);
    }

    [Fact]
    public void Prompt_Switch_Goes_To_System_Message()
    {
        var b = Body();
        Reasoning.Apply(b, ReasoningStyle.PromptSwitch, ReasoningLevel.Off, "SmolLM3", null, true, null);
        var first = (JObject)((JArray)b["messages"]!)[0];
        Assert.Equal("system", (string?)first["role"]);
        Assert.Equal("/no_think", (string?)first["content"]);
    }

    [Fact]
    public void None_And_AlwaysOn_Send_Nothing()
    {
        var b = Body();
        Reasoning.Apply(b, ReasoningStyle.Auto, ReasoningLevel.Off, "gemma-3-12b-it", null, true, null);
        Reasoning.Apply(b, ReasoningStyle.AlwaysOn, ReasoningLevel.Off, "DeepSeek-R1", null, true, null);
        Assert.Null(b["chat_template_kwargs"]);
        Assert.Null(b["reasoning_effort"]);
    }

    [Fact]
    public void Old_Thinking_Flag_Maps_To_Level()
    {
        var r = new ChatRequest { Thinking = false };
        Assert.Equal(ReasoningLevel.Off, r.Reasoning);
        var s = new ChatSession { Thinking = true };
        Assert.Equal(ReasoningLevel.Medium, s.EffectiveReasoning);
    }

    [Theory]
    [InlineData("<think>plan</think>\n\nAnswer", "plan", "Answer", false)]
    [InlineData("<|channel>thought\nLet me think<channel|>Paris", "Let me think", "Paris", false)]
    [InlineData("<|channel|>analysis<|message|>reason<|end|><|start|>assistant<|channel|>final<|message|>42<|return|>", "reason", "42", false)]
    [InlineData("<|channel>thought\nstill thinking", "still thinking", "", true)]
    [InlineData("Plain answer <think> in the middle", "", "Plain answer <think> in the middle", false)]
    public void Splits_Reasoning_Wrappers(string text, string reasoning, string content, bool open)
    {
        var (r, c, o) = ThinkSplitter.Split(text);
        Assert.Equal(reasoning, r);
        Assert.Equal(content, c);
        Assert.Equal(open, o);
    }

    [Theory]
    [InlineData("<|chan", true)]
    [InlineData("<|channel>thought\nabc", true)]
    [InlineData("  <think>", true)]
    [InlineData("Hello", false)]
    public void Detects_Wrapper_Start_While_Streaming(string text, bool expected) =>
        Assert.Equal(expected, ThinkSplitter.StartsWithWrapper(text));
}