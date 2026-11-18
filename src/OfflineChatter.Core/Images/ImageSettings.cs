using System.Collections.Generic;

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

    public ImageLaunchSettings Clone() => (ImageLaunchSettings)MemberwiseClone();
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

    public string? ModelName { get; set; }
    public string? Family { get; set; }

    public ImageGenParams Clone() => (ImageGenParams)MemberwiseClone();

    /// <summary>Значения пресета (размер, шаги, CFG, сэмплер); промпты и режим сохраняются.</summary>
    public void ApplyPreset(ImagePreset p)
    {
        Width = p.Width;
        Height = p.Height;
        Steps = p.Steps;
        Cfg = p.Cfg;
        Sampler = p.Sampler;
        Scheduler = p.Scheduler ?? string.Empty;
        FlowShift = p.FlowShift;
        Family = p.Id;
        if (Mode == ImageMode.Edit && !p.SupportsRefImages) Mode = ImageMode.TextToImage;
    }
}
