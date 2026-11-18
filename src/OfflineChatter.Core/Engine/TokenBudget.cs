using System;
using System.Collections.Generic;
using System.Globalization;

namespace OfflineChatter.Core.Engine;

/// <summary>Предупреждение о сочетании контекста и лимита ответа.</summary>
public enum TokenBudgetWarning
{
    None,
    /// <summary>Лимит ответа не меньше всего контекста: на вопрос и историю места не останется.</summary>
    AnswerFillsContext,
    /// <summary>Ответ займёт больше половины контекста: длинная беседа быстро упрётся в предел.</summary>
    AnswerTakesMostOfContext,
    /// <summary>Контекст больше, чем модель видела при обучении: качество за пределом падает.</summary>
    ContextAboveTrained,
}

/// <summary>Быстрые значения и понятные пересчёты для полей «Контекст» и «Лимит ответа».</summary>
public static class TokenBudget
{
    /// <summary>0 — «авто» (подбор по памяти).</summary>
    public static IReadOnlyList<int> ContextPresets { get; } = new[] { 0, 4096, 8192, 16384, 32768, 65536, 131072 };

    /// <summary>-1 — без ограничения (модель пишет, пока не закончит мысль или не кончится контекст).</summary>
    public static IReadOnlyList<int> MaxTokensPresets { get; } = new[] { -1, 512, 1024, 2048, 4096, 8192, 16384 };

    /// <summary>4096 → «4K», 131072 → «128K», 512 → «512».</summary>
    public static string Short(int tokens) =>
        tokens >= 1024 && tokens % 1024 == 0 ? (tokens / 1024).ToString(CultureInfo.InvariantCulture) + "K"
        : tokens >= 1000 ? (tokens / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : tokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>Примерно слов английского текста (≈ 0,75 слова на токен; на русском — около половины).</summary>
    public static int ApproxWords(int tokens) => tokens <= 0 ? 0 : (int)Math.Round(tokens * 0.75);

    /// <summary>Примерно страниц (≈ 500 слов на страницу), не меньше 0,1.</summary>
    public static double ApproxPages(int tokens) => tokens <= 0 ? 0 : Math.Max(0.1, Math.Round(ApproxWords(tokens) / 500.0, 1));

    /// <param name="context">Размер контекста; 0 или меньше — неизвестен (авто).</param>
    /// <param name="maxTokens">Лимит ответа; -1 — без лимита.</param>
    /// <param name="trained">Контекст обучения модели, если известен.</param>
    public static TokenBudgetWarning Check(int context, int maxTokens, int? trained)
    {
        if (context > 0 && trained is > 0 && context > trained) return TokenBudgetWarning.ContextAboveTrained;
        if (context <= 0 || maxTokens <= 0) return TokenBudgetWarning.None;
        if (maxTokens >= context) return TokenBudgetWarning.AnswerFillsContext;
        if (maxTokens > context / 2) return TokenBudgetWarning.AnswerTakesMostOfContext;
        return TokenBudgetWarning.None;
    }
}
