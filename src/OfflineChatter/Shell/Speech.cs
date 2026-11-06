using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Shell;

/// <summary>Озвучивание ответов системным синтезатором (SAPI, есть на Win7–11). Голос выбирается по языку текста.</summary>
public static class Speech
{
    private static SpeechSynthesizer? _synth;

    public static bool IsSpeaking => _synth?.State == SynthesizerState.Speaking;

    public static void Toggle(string markdown)
    {
        try
        {
            _synth ??= new SpeechSynthesizer();
            if (_synth.State == SynthesizerState.Speaking)
            {
                _synth.SpeakAsyncCancelAll();
                return;
            }
            var text = ToSpeakable(markdown);
            if (text.Length == 0) return;
            var cyrillic = text.Count(c => c >= 'А' && c <= 'я') > text.Length / 5;
            var voice = _synth.GetInstalledVoices().Where(v => v.Enabled)
                .FirstOrDefault(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == (cyrillic ? "ru" : "en"));
            if (voice != null) _synth.SelectVoice(voice.VoiceInfo.Name);
            _synth.SpeakAsync(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            Log.Warn("Speech", "Speech synthesis unavailable", ex);
        }
    }

    /// <summary>Убираем разметку: код целиком, формулы, символы Markdown.</summary>
    public static string ToSpeakable(string md)
    {
        var s = Regex.Replace(md, "```[\\s\\S]*?```", " ");
        s = Regex.Replace(s, @"\$\$[\s\S]*?\$\$", " ");
        s = Regex.Replace(s, @"\$[^$\n]+\$", " ");
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", " ");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"[*_`#>|~]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }
}
