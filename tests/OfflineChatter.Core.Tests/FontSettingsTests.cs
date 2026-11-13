using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class FontSettingsTests
{
    [Fact]
    public void Defaults_Keep_The_Previous_Sizes()
    {
        // Fluent: прежние размеры разметки — 13 / 12 / 11 / 14 / 15, заголовки 20 и 24, сообщения 14, код 12,5
        Assert.Equal(new TypeScale(13, 12, 11, 14, 15, 20, 24, 14, 12.5, 12.5, null), Typography.Resolve(new FontSettings(), 13, 20, 24));
        // Системные скины: текст 12 (9 pt), заголовки 16
        var native = Typography.Resolve(new FontSettings(), 12, 16, 16);
        Assert.Equal((12.0, 12.0, 16.0, 16.0), (native.Ui, native.Caption, native.Title, native.PageTitle));
    }

    [Fact]
    public void Ui_Size_Shifts_The_Whole_Interface_Scale()
    {
        var s = Typography.Resolve(new FontSettings { UiSize = 15 }, 12, 16, 16);
        Assert.Equal(new TypeScale(15, 15, 14, 17, 18, 19, 19, 14, 12.5, 12.5, null), s);

        // Сообщения и код задаются отдельно; интервал — множитель размера сообщений
        var chat = Typography.Resolve(new FontSettings { ChatSize = 16, CodeSize = 14, ChatLineSpacing = 1.6 }, 13, 20, 24);
        Assert.Equal((16.0, 14.5, 14.0, (double?)25.6), (chat.Chat, chat.ChatSmall, chat.Code, chat.ChatLineHeight));
    }

    [Theory]
    [InlineData(TextSharpness.Auto, false, 1.25, true)]   // Win7/8: по пикселям при любом масштабе
    [InlineData(TextSharpness.Auto, true, 1.0, true)]     // Win10/11 при 100 %
    [InlineData(TextSharpness.Auto, true, 1.5, false)]    // Win10/11 на HiDPI — точные контуры
    [InlineData(TextSharpness.Sharp, true, 2.0, true)]
    [InlineData(TextSharpness.Smooth, false, 1.0, false)]
    public void Display_Mode(TextSharpness sharpness, bool modernOs, double dpi, bool expected) =>
        Assert.Equal(expected, Typography.UseDisplayMode(sharpness, modernOs, dpi));

    [Fact]
    public void Normalize_Clamps_And_Clears_Garbage()
    {
        var f = new FontSettings
        {
            UiFamily = "  ", ChatFamily = " Georgia ", CodeFamily = new string('x', 300),
            UiSize = 99, ChatSize = 3, CodeSize = 12.3, ChatLineSpacing = double.NaN,
            Sharpness = (TextSharpness)42, Smoothing = (TextSmoothing)(-1),
        };
        f.Normalize();
        Assert.Null(f.UiFamily);
        Assert.Equal("Georgia", f.ChatFamily);
        Assert.Null(f.CodeFamily);
        Assert.Equal((20.0, 10.0, 12.5), (f.UiSize!.Value, f.ChatSize!.Value, f.CodeSize!.Value));
        Assert.Null(f.ChatLineSpacing);
        Assert.Equal((TextSharpness.Auto, TextSmoothing.Auto), (f.Sharpness, f.Smoothing));

        // Старый settings.json без раздела шрифтов
        var s = new AppSettings();
        s.Appearance.Fonts = null!;
        SettingsStore.Normalize(s);
        Assert.NotNull(s.Appearance.Fonts);
        Assert.Null(s.Appearance.Fonts.UiSize);
    }
}
