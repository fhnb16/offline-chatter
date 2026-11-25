using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineChatter.Core.Images;

/// <summary>Настройки генерации изображений (settings.json → Images).</summary>
public sealed class ImageSettings
{
    /// <summary>Id активной сборки stable-diffusion.cpp (путь к папке).</summary>
    public string? ActiveEngineId { get; set; }

    /// <summary>Подключённые пользователем папки со своими сборками (с sd-server.exe).</summary>
    public List<string> ExternalEngines { get; set; } = new();

    /// <summary>Модель для генерации по умолчанию (Id в image-models.json).</summary>
    public string? ActiveModelId { get; set; }

    /// <summary>Параметры запуска sd-server; пустые значения берутся из пресета семейства модели.</summary>
    public ImageLaunchSettings Launch { get; set; } = new();

    /// <summary>Последние параметры генерации (страница «Изображения» открывается с ними).</summary>
    public ImageGenParams? LastParams { get; set; }

    /// <summary>
    /// Освобождать видеопамять: перед запуском генератора останавливать llama-server и наоборот.
    /// Не действует, если включён публичный API соответствующего сервера (его клиенты ждут, что он работает).
    /// </summary>
    public bool ShareVram { get; set; } = true;

    /// <summary>Куда сохранять картинки (пусто — папка images рядом с данными программы).</summary>
    public string? OutputFolder { get; set; }

    // ---------- Генерация: превью, память, входные картинки ----------

    /// <summary>Живое превью по шагам: "auto" (TAE, если есть, иначе проекция латентов), "none", "proj", "tae", "vae".</summary>
    public string PreviewMode { get; set; } = "auto";

    /// <summary>Каждые сколько шагов обновлять превью.</summary>
    public int PreviewInterval { get; set; } = 1;

    /// <summary>
    /// Плитки VAE в запросе, когда выход или вход больше <see cref="ImageInputPolicy.TilingThreshold"/>: кодирование и
    /// декодирование 2K-картинок без плиток — частая причина нехватки видеопамяти до первого шага или на 100 %.
    /// </summary>
    public bool AutoVaeTiling { get; set; } = true;

    /// <summary>Размер плитки VAE в пикселях.</summary>
    public int VaeTileSize { get; set; } = 512;

    /// <summary>
    /// Уменьшать референсы по длинной стороне в зависимости от их числа (<see cref="ImageInputPolicy.RefMaxSide"/>):
    /// фото с камеры раздувает и зрение кодировщика, и внимание диффузионной модели.
    /// </summary>
    public bool LimitInputSize { get; set; } = true;

    // ---------- Публичный OpenAI-совместимый API ----------

    /// <summary>Открыть генератор другим приложениям: /v1/images/generations, /v1/images/edits, /sdapi/v1/*.</summary>
    public bool ExposeApi { get; set; }

    /// <summary>Порт публичного API (сам sd-server всегда слушает 127.0.0.1 на случайном порту).</summary>
    public int Port { get; set; } = DefaultPort;

    public const int DefaultPort = 8081;

    /// <summary>127.0.0.1, 0.0.0.0 или IPv4 адаптера.</summary>
    public string ListenAddress { get; set; } = Engine.EngineSettings.Loopback;

    /// <summary>Bearer-ключ публичного API, зашифрован через Secrets. У sd-server ключа нет — его проверяет программа.</summary>
    public string ApiKeyProtected { get; set; } = string.Empty;
}

/// <summary>Параметры запуска sd-server. null — «авто»: значение пресета семейства или умолчание движка.</summary>
public sealed class ImageLaunchSettings
{
    /// <summary>Потоки CPU (-t); null — по числу физических ядер (решает движок).</summary>
    public int? Threads { get; set; }

    /// <summary>Держать веса в ОЗУ и подгружать в видеопамять по частям (--offload-to-cpu): медленнее, но влезает больше.</summary>
    public bool? OffloadToCpu { get; set; }

    /// <summary>Текстовые кодировщики на CPU (--clip-on-cpu): экономит видеопамять, немного медленнее разбор промпта.</summary>
    public bool? ClipOnCpu { get; set; }

    /// <summary>VAE на CPU (--vae-on-cpu): экономит видеопамять, декодирование заметно медленнее.</summary>
    public bool? VaeOnCpu { get; set; }

    /// <summary>Декодировать картинку плитками (--vae-tiling): меньше пик видеопамяти на больших разрешениях.</summary>
    public bool? VaeTiling { get; set; }

    /// <summary>Flash attention: "auto", "on", "off".</summary>
    public string FlashAttention { get; set; } = "auto";

    /// <summary>Пересчитать веса в тип при загрузке (--type: f16, q8_0, q4_0…); пусто — как в файле.</summary>
    public string? WeightType { get; set; }

    /// <summary>Генератор случайных чисел (--rng): "cuda" (как у PyTorch на GPU) или "cpu"; пусто — по умолчанию.</summary>
    public string? Rng { get; set; }

    /// <summary>Папка с LoRA (--lora-model-dir).</summary>
    public string? LoraDir { get; set; }

    /// <summary>Дополнительные --model-args (ключ=значение через запятую) поверх пресета.</summary>
    public string? ModelArgs { get; set; }

    /// <summary>Передавать параметры генерации пресета серверу при запуске — их получат и внешние клиенты API.</summary>
    public bool ServerDefaults { get; set; } = true;

    /// <summary>Произвольные аргументы командной строки.</summary>
    public string? ExtraArgs { get; set; }

    /// <summary>
    /// Бюджет видеопамяти для весов и буферов (--max-vram): «6.8» — ГиБ на устройство, «cuda0=6.8» — по устройствам,
    /// «-1» — оставить свободным около 1 ГиБ. Это не жёсткий предел: контекст драйвера в него не входит.
    /// </summary>
    public string? MaxVram { get; set; }

    /// <summary>Сколько результатов кодирования промпта (и картинок) помнить (--conditioning-cache-size); null — значение пресета.</summary>
    public int? ConditioningCacheSize { get; set; }

    /// <summary>Если есть и VAE, и TAE: TAE только для превью, итог — полным VAE (--taesd-preview-only).</summary>
    public bool TaePreviewOnly { get; set; } = true;

    /// <summary>Отображать файлы весов в память (--mmap): меньше копий в ОЗУ при 16 ГБ.</summary>
    public bool? Mmap { get; set; }

    public ImageLaunchSettings Clone() => (ImageLaunchSettings)MemberwiseClone();
}

/// <summary>Готовые наборы параметров памяти для «Движок → Изображения» (одна кнопка).</summary>
public enum ImageMemoryProfile
{
    /// <summary>Правка и генерация на 8 ГБ: веса в ОЗУ, бюджет видеопамяти, flash attention, кэш промптов на 8.</summary>
    Balanced,
    /// <summary>То же, плюс отображение файлов в память — для 16 ГБ ОЗУ.</summary>
    SaveRam,
    /// <summary>Кодировщик на процессоре: 0 МБ видеопамяти на него; для 32 ГБ ОЗУ и кодировщика Q8_0.</summary>
    EncoderOnCpu,
}

public static class ImageMemoryProfiles
{
    /// <summary>
    /// Бюджет видеопамяти: 85 % объёма карты (8 ГБ → 6.8), минимум 2 ГиБ. На Windows часть памяти держат рабочий стол
    /// и драйвер, поэтому полный объём не задаётся.
    /// </summary>
    public static double VramBudgetGb(long dedicatedBytes)
    {
        var gb = dedicatedBytes / (double)(1L << 30);
        return Math.Max(2.0, Math.Round(gb * 0.85, 1));
    }

    /// <summary>Заполнить параметры запуска значениями профиля; остальное (потоки, LoRA, свои аргументы) не трогается.</summary>
    public static void Apply(ImageLaunchSettings s, ImageMemoryProfile profile, long dedicatedVram)
    {
        s.OffloadToCpu = true;
        s.FlashAttention = "on";
        s.ConditioningCacheSize = 8;
        s.VaeTiling = null; // плитки включаются по размеру в каждом запросе
        s.MaxVram = dedicatedVram > 0 ? VramBudgetGb(dedicatedVram).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : null;
        s.Mmap = profile == ImageMemoryProfile.SaveRam ? true : null;
        s.ClipOnCpu = profile == ImageMemoryProfile.EncoderOnCpu ? true : null;
    }
}

/// <summary>Режим страницы генерации.</summary>
public enum ImageMode
{
    /// <summary>Картинка из текста.</summary>
    TextToImage,
    /// <summary>Редактирование по референсам (Kontext, Qwen-Image-Edit, FLUX.2): модель «видит» картинки.</summary>
    Edit,
    /// <summary>Перерисовка исходной картинки с силой изменения (img2img).</summary>
    ImageToImage,
}

/// <summary>Параметры одной генерации (сохраняются между запусками и пишутся рядом с картинкой).</summary>
public sealed class ImageGenParams
{
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public ImageMode Mode { get; set; } = ImageMode.TextToImage;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Steps { get; set; } = 20;
    public double Cfg { get; set; } = 1.0;
    public string Sampler { get; set; } = "euler";
    public string Scheduler { get; set; } = string.Empty;
    public double? FlowShift { get; set; }
    /// <summary>-1 — случайное.</summary>
    public long Seed { get; set; } = -1;
    public int BatchCount { get; set; } = 1;
    /// <summary>Пропуск последних слоёв CLIP (SD 1.x/аниме-модели); null — по умолчанию.</summary>
    public int? ClipSkip { get; set; }
    /// <summary>Сила изменения для img2img (0 — без изменений, 1 — нарисовать заново).</summary>
    public double Strength { get; set; } = 0.75;

    /// <summary>Свои сигмы через запятую (Turbo: фиксированное расписание на 8 шагов); пусто — расписание сэмплера.</summary>
    public string Sigmas { get; set; } = string.Empty;

    /// <summary>Кэш шагов диффузии: "" (выключен), "easycache", "dbcache", "taylorseer", "cache-dit", "ucache", "spectrum".</summary>
    public string CacheMode { get; set; } = string.Empty;

    /// <summary>Прозрачный фон: промпт оборачивается в рекомендованную авторами форму (Qwen-Image 2.1 решает по тексту).</summary>
    public bool Transparent { get; set; }

    /// <summary>Как подогнать исходник img2img (и маску) под размер холста.</summary>
    public ImageFit InitFit { get; set; } = ImageFit.CropResize;

    /// <summary>Вместо исходника отправить его контуры (Canny): композиция по линиям, цвета — заново.</summary>
    public bool InitCanny { get; set; }

    /// <summary>Перерисовывать то, что вне маски.</summary>
    public bool MaskInvert { get; set; }

    /// <summary>Размер по первому референсу (пропорции фото, кратно модели).</summary>
    public bool SizeFromRef { get; set; } = true;

    /// <summary>Масштаб размера от базового: 1 — 1K (быстро), 2 — 2K (родной размер правки Qwen-Image 2.1).</summary>
    public int SizeScale { get; set; } = 1;

    public List<ImageLora> Loras { get; set; } = new();

    public string? ModelName { get; set; }
    public string? Family { get; set; }

    public ImageGenParams Clone()
    {
        var c = (ImageGenParams)MemberwiseClone();
        c.Loras = Loras.Select(l => new ImageLora { Path = l.Path, Multiplier = l.Multiplier }).ToList();
        return c;
    }

    /// <summary>Значения пресета (размер, шаги, CFG, сэмплер, сигмы); промпты и режим сохраняются.</summary>
    public void ApplyPreset(ImagePreset p)
    {
        Width = p.Width;
        Height = p.Height;
        Steps = p.Steps;
        Cfg = p.Cfg;
        Sampler = p.Sampler;
        Scheduler = p.Scheduler ?? string.Empty;
        FlowShift = p.FlowShift;
        Sigmas = p.SigmasText ?? string.Empty;
        if (p.IsDistilled) CacheMode = string.Empty;
        Family = p.Id;
        if (Mode == ImageMode.Edit && !p.SupportsRefImages) Mode = ImageMode.TextToImage;
        if (!p.SupportsAlpha) Transparent = false;
    }
}

/// <summary>LoRA для запроса: путь относительно папки LoRA сервера и сила.</summary>
public sealed class ImageLora
{
    public string Path { get; set; } = string.Empty;
    public double Multiplier { get; set; } = 1.0;
}
