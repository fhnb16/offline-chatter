using System.Linq;
using OfflineChatter.Core.Chat;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class ChatTitlesTests
{
    [Theory]
    [InlineData("Как приготовить борщ?", "Как приготовить борщ?")]
    [InlineData("  **Помоги** с `regex`\n\nдля телефонов  ", "Помоги с regex для телефонов")]
    [InlineData("# Заголовок\n- пункт один\n- пункт два", "Заголовок пункт один пункт два")]
    [InlineData("Смотри [документацию](https://example.com/docs) по API", "Смотри документацию по API")]
    [InlineData("Почему падает?\n```\nNullReferenceException at Foo.Bar()\n```", "Почему падает?")]
    [InlineData("```\nint x = 1;\n```", "int x = 1;")]
    public void First_Words(string text, string expected) => Assert.Equal(expected, ChatTitles.FromText(text));

    [Fact]
    public void First_Words_Are_Cut_On_A_Word_Boundary()
    {
        var title = ChatTitles.FromText("Объясни разницу между процессами и потоками в операционной системе Windows подробно")!;
        Assert.Equal("Объясни разницу между процессами и потоками в…", title);
        Assert.True(title.Length <= 49);
        Assert.Null(ChatTitles.FromText("   \n "));
        Assert.Null(ChatTitles.FromText(null));
    }

    [Theory]
    [InlineData("Рецепт классического борща", "Рецепт классического борща")]
    [InlineData("\"Borscht recipe.\"", "Borscht recipe")]
    [InlineData("«Рецепт борща»", "Рецепт борща")]
    [InlineData("Title: Borscht recipe", "Borscht recipe")]
    [InlineData("Название — Рецепт борща!", "Рецепт борща")]
    [InlineData("**Рецепт борща**", "Рецепт борща")]
    [InlineData("# Рецепт борща", "Рецепт борща")]
    [InlineData("Here is a short title:\nBorscht recipe", "Borscht recipe")]
    [InlineData("<think>The user asks about soup.</think>\nBorscht recipe", "Borscht recipe")]
    [InlineData("soup...</think>Borscht recipe", "Borscht recipe")]
    [InlineData("Почему падает Foo?", "Почему падает Foo?")]
    public void Model_Answer_Is_Cleaned(string raw, string expected) => Assert.Equal(expected, ChatTitles.Clean(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("<think>Let me think about the title for a while</think>")]
    [InlineData("Sure! The conversation is about how to cook a traditional beetroot soup with beef and sour cream at home")]
    public void Not_A_Title(string raw) => Assert.Null(ChatTitles.Clean(raw));

    [Fact]
    public void Request_Is_Short_And_Without_Reasoning()
    {
        var r = ChatTitles.BuildRequest(new string('q', 5000), new string('a', 5000), "model-x");
        Assert.Equal("model-x", r.Model);
        Assert.Equal(ReasoningLevel.Off, r.Reasoning);
        Assert.Equal(64, r.MaxTokens);
        Assert.Null(r.Tools);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User }, r.Messages.Select(m => m.Role));
        Assert.True(r.Messages[1].Content.Length < 2000);
        Assert.EndsWith("Title:", r.Messages[1].Content);

        // Без ответа — только вопрос
        Assert.DoesNotContain("Assistant answer", ChatTitles.BuildRequest("Вопрос", null, null).Messages[1].Content);

        // Русский вопрос — язык названия назван прямо; код в вопросе не перевешивает
        Assert.Contains("in Russian", ChatTitles.BuildRequest("Напиши регулярку для телефонов: ^(\\+7|8)\\d{10}$", null, null).Messages[0].Content);
        Assert.Contains("in Russian", ChatTitles.BuildRequest("Почему падает?\n```\nNullReferenceException at Foo.Bar.Baz()\n```", null, null).Messages[0].Content);
        Assert.DoesNotContain("Russian", ChatTitles.BuildRequest("Why does my async method deadlock?", null, null).Messages[0].Content);
    }

    [Fact]
    public void Cyrillic_Detection()
    {
        Assert.True(ChatTitles.IsMostlyCyrillic("Как дела?"));
        Assert.False(ChatTitles.IsMostlyCyrillic("How are you?"));
        Assert.False(ChatTitles.IsMostlyCyrillic("12345"));
    }
}
