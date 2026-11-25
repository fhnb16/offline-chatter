using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OfflineChatter.Core.Images;

/// <summary>Роль файла модели в stable-diffusion.cpp (у каждой — свой флаг командной строки).</summary>
public enum ImageComponent
{
    /// <summary>Цельный чекпойнт (SD 1.x/2.x, SDXL, SD3): --model.</summary>
    Checkpoint,
    /// <summary>Только диффузионная часть (FLUX, Qwen-Image, Z-Image…): --diffusion-model.</summary>
    DiffusionModel,
    /// <summary>Декодер латентов в картинку: --vae.</summary>
    Vae,
    /// <summary>Текстовый кодировщик CLIP-L: --clip_l.</summary>
    ClipL,
    /// <summary>Текстовый кодировщик CLIP-G (SDXL/SD3): --clip_g.</summary>
    ClipG,
    /// <summary>Текстовый кодировщик T5-XXL: --t5xxl.</summary>
    T5xxl,
    /// <summary>Языковая модель-кодировщик (Qwen2.5-VL, Qwen3, Mistral): --llm.</summary>
    Llm,
    /// <summary>Зрение языковой модели (mmproj) для моделей редактирования: --llm_vision.</summary>
    LlmVision,
    /// <summary>CLIP Vision (Wan I2V): --clip_vision.</summary>
    ClipVision,
    /// <summary>
    /// Маленький автокодировщик (TAESD, TAEQI2.1): --taesd. Рядом с полным VAE — только для живого превью
    /// (--taesd-preview-only), без VAE — вместо него.
    /// </summary>
    Tae,
}

public enum ImageFamily
{
    Sd15,
    Sd2,
    Sdxl,
    SdxlTurbo,
    Sd3,
    Flux1Dev,
    Flux1Schnell,
    FluxKontext,
    Flux2,
    Flux2Klein,
    Chroma,
    QwenImage,
    QwenImageEdit,
    QwenImageEdit2511,
    QwenImage21,
    QwenImage21Turbo,
    ZImage,
    ZImageTurbo,
    Custom,
}

/// <summary>
/// Пресет семейства: какие файлы нужны, разумные параметры генерации и запуска.
/// Значения — из документации stable-diffusion.cpp (docs/models/*.md) и рецептов сервера (recipes/defaults.md).
/// </summary>
public sealed record ImagePreset(
    ImageFamily Family,
    string Title,
    IReadOnlyList<ImageComponent> Required,
    IReadOnlyList<ImageComponent> Optional,
    int Width,
    int Height,
    int Steps,
    double Cfg,
    string Sampler,
    string? Scheduler = null,
    double? FlowShift = null,
    bool OffloadToCpu = false,
    bool ClipOnCpu = false,
    bool VaeTiling = false,
    bool SupportsRefImages = false,
    bool SupportsImg2Img = true,
    string? ModelArgs = null,
    string? Recipe = null,
    IReadOnlyList<ImageComponent>? RequiredAny = null,
    int SizeMultiple = 8,
    IReadOnlyList<double>? Sigmas = null,
    bool SupportsAlpha = false,
    int? ConditioningCache = null,
    int? NativeEditSide = null)
{
    public string Id => Family.ToString();

    /// <summary>Негативный промпт работает только при CFG > 1 (дистиллированные модели его игнорируют).</summary>
    public bool UsesNegativePrompt => Cfg > 1.0;

    /// <summary>Площадь базового разрешения — от неё считаются размеры для других пропорций.</summary>
    public int BaseArea => Width * Height;

    public IEnumerable<ImageComponent> AllComponents => Required.Concat(Optional);

    /// <summary>Шаг сетки размеров для кнопок пропорций: кратность модели, но не мельче 64, если модель не требует иного.</summary>
    public int RoundTo => SizeMultiple >= 32 ? SizeMultiple : 64;

    /// <summary>
    /// Дистиллированная модель (Turbo, Lightning, schnell): мало шагов и CFG 1. Кэш шагов (EasyCache, DBCache) ей вредит,
    /// CFG выше 1 ломает картинку.
    /// </summary>
    public bool IsDistilled => Steps <= 8 && Cfg <= 1.0;

    /// <summary>Картинки внутри промпта (&lt;image:ID&gt;) понимают только кодировщики Qwen-VL со зрением (mmproj).</summary>
    public bool SupportsPromptImages => AllComponents.Contains(ImageComponent.LlmVision);

    public bool UsesNumberedReferenceTags => Family is ImageFamily.QwenImage21 or ImageFamily.QwenImage21Turbo;

    /// <summary>Расписание шума модели строкой «1,0.97,…,0» (для поля и --sigmas); null — нет своего.</summary>
    public string? SigmasText => Sigmas == null ? null : ImageSigmas.Format(Sigmas);
}

/// <summary>Свои значения сигм: разбор и запись строкой через запятую.</summary>
public static class ImageSigmas
{
    /// <summary>Фиксированное расписание Qwen-Image 2.1 Turbo (model_index.json; в sd.cpp — пресет qwen-image-2.1-turbo).</summary>
    public static IReadOnlyList<double> QwenImage21Turbo { get; } = new[] { 1.0, 0.978453, 0.95418, 0.926626, 0.89508, 0.845148, 0.704534, 0.414568, 0.0 };

    public static string Format(IEnumerable<double> values) =>
        string.Join(",", values.Select(v => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>«1, 0.9; 0,5» → [1, 0.9, 0.5]. Пусто — null; любое нечисло — null (поле подсветит ошибку).</summary>
    public static IReadOnlyList<double>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text!.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<double>();
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0 || double.IsNaN(v))
                return null;
            list.Add(v);
        }
        return list.Count >= 2 ? list : null;
    }

    /// <summary>Число шагов, которое даст расписание: на одно меньше числа сигм.</summary>
    public static int Steps(IReadOnlyList<double> sigmas) => Math.Max(1, sigmas.Count - 1);
}

public static class ImagePresets
{
    private static readonly ImageComponent[] None = Array.Empty<ImageComponent>();
    private static readonly ImageComponent[] Ckpt = { ImageComponent.Checkpoint };
    private static readonly ImageComponent[] OptVae = { ImageComponent.Vae };

    public static IReadOnlyList<ImagePreset> All { get; } = new[]
    {
        new ImagePreset(ImageFamily.Sd15, "Stable Diffusion 1.5", Ckpt, OptVae, 512, 512, 20, 7.0, "euler_a", Recipe: "sd1-checkpoint"),
        new ImagePreset(ImageFamily.Sd2, "Stable Diffusion 2.x", Ckpt, OptVae, 768, 768, 20, 7.0, "euler_a"),
        new ImagePreset(ImageFamily.Sdxl, "SDXL", Ckpt, OptVae, 1024, 1024, 25, 5.0, "euler_a", VaeTiling: true, Recipe: "sdxl-checkpoint"),
        new ImagePreset(ImageFamily.SdxlTurbo, "SDXL Turbo / Lightning", Ckpt, OptVae, 512, 512, 4, 1.0, "euler_a"),
        new ImagePreset(ImageFamily.Sd3, "Stable Diffusion 3 / 3.5", Ckpt,
            new[] { ImageComponent.ClipL, ImageComponent.ClipG, ImageComponent.T5xxl, ImageComponent.Vae }, 1024, 1024, 20, 4.5, "euler", ClipOnCpu: true, Recipe: "sd35-large"),
        new ImagePreset(ImageFamily.Flux1Dev, "FLUX.1 dev",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.ClipL, ImageComponent.T5xxl }, None, 1024, 1024, 20, 1.0, "euler",
            ClipOnCpu: true, SupportsImg2Img: true, Recipe: "flux1-dev"),
        new ImagePreset(ImageFamily.Flux1Schnell, "FLUX.1 schnell",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.ClipL, ImageComponent.T5xxl }, None, 1024, 1024, 4, 1.0, "euler",
            ClipOnCpu: true, Recipe: "flux1-schnell"),
        new ImagePreset(ImageFamily.FluxKontext, "FLUX.1 Kontext (edit)",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.ClipL, ImageComponent.T5xxl }, None, 1024, 1024, 20, 1.0, "euler",
            ClipOnCpu: true, SupportsRefImages: true, Recipe: "flux1-kontext"),
        new ImagePreset(ImageFamily.Flux2, "FLUX.2 dev",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 20, 1.0, "euler",
            OffloadToCpu: true, SupportsRefImages: true, Recipe: "flux2-dev"),
        new ImagePreset(ImageFamily.Flux2Klein, "FLUX.2 klein",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 4, 1.0, "euler",
            SupportsRefImages: true, Recipe: "flux2-klein-4b"),
        new ImagePreset(ImageFamily.Chroma, "Chroma",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.T5xxl }, None, 1024, 1024, 20, 4.0, "euler",
            ClipOnCpu: true, ModelArgs: "chroma_use_dit_mask=false", Recipe: "chroma"),
        new ImagePreset(ImageFamily.QwenImage, "Qwen-Image",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 20, 2.5, "euler",
            FlowShift: 3, OffloadToCpu: true, Recipe: "qwen-image"),
        new ImagePreset(ImageFamily.QwenImageEdit, "Qwen-Image-Edit (2509)",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, new[] { ImageComponent.LlmVision }, 1024, 1024, 20, 2.5, "euler",
            FlowShift: 3, OffloadToCpu: true, SupportsRefImages: true, Recipe: "qwen-image-edit-2509"),
        new ImagePreset(ImageFamily.QwenImageEdit2511, "Qwen-Image-Edit 2511",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, new[] { ImageComponent.LlmVision }, 1024, 1024, 20, 2.5, "euler",
            FlowShift: 3, OffloadToCpu: true, SupportsRefImages: true, ModelArgs: "qwen_image_zero_cond_t=true", Recipe: "qwen-image-edit-2511"),
        // Qwen-Image 2.1: генерация и правка одной моделью, кодировщик Qwen3-VL-8B (+ mmproj для правки), свой VAE или TAEQI2.1,
        // размеры кратны 32, есть альфа-канал. Умолчания — рецепты sd-server qwen-image-21 / qwen-image-21-turbo.
        new ImagePreset(ImageFamily.QwenImage21, "Qwen-Image 2.1",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Llm }, new[] { ImageComponent.LlmVision, ImageComponent.Vae, ImageComponent.Tae },
            1024, 1024, 20, 1.0, "euler", OffloadToCpu: true, SupportsRefImages: true, Recipe: "qwen-image-21",
            RequiredAny: new[] { ImageComponent.Vae, ImageComponent.Tae }, SizeMultiple: 32, SupportsAlpha: true, ConditioningCache: 8, NativeEditSide: 2048),
        new ImagePreset(ImageFamily.QwenImage21Turbo, "Qwen-Image 2.1 Turbo",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Llm }, new[] { ImageComponent.LlmVision, ImageComponent.Vae, ImageComponent.Tae },
            1024, 1024, 8, 1.0, "euler", OffloadToCpu: true, SupportsRefImages: true, Recipe: "qwen-image-21-turbo",
            RequiredAny: new[] { ImageComponent.Vae, ImageComponent.Tae }, SizeMultiple: 32, Sigmas: ImageSigmas.QwenImage21Turbo, SupportsAlpha: true,
            ConditioningCache: 8, NativeEditSide: 2048),
        new ImagePreset(ImageFamily.ZImage, "Z-Image",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 20, 4.0, "euler",
            OffloadToCpu: true, Recipe: "z-image"),
        new ImagePreset(ImageFamily.ZImageTurbo, "Z-Image Turbo",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 8, 1.0, "euler",
            OffloadToCpu: true, Recipe: "z-image-turbo"),
        new ImagePreset(ImageFamily.Custom, "Custom", None,
            new[] { ImageComponent.Checkpoint, ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.ClipL, ImageComponent.ClipG, ImageComponent.T5xxl, ImageComponent.Llm, ImageComponent.LlmVision, ImageComponent.Tae },
            512, 512, 20, 7.0, "euler_a"),
    };

    public static ImagePreset Get(ImageFamily family) => All.First(p => p.Family == family);

    public static ImagePreset Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Get(ImageFamily.Custom);

    /// <summary>Методы сэмплирования stable-diffusion.cpp (значения --sampling-method).</summary>
    public static IReadOnlyList<string> Samplers { get; } = new[]
    {
        "euler", "euler_a", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "ipndm", "ipndm_v", "lcm", "ddim_trailing", "tcd",
        "res_multistep", "res_2s", "er_sde", "dpm++2m_sde", "lms",
    };

    /// <summary>Расписания шума (--scheduler); пусто — по умолчанию модели.</summary>
    public static IReadOnlyList<string> Schedulers { get; } = new[]
    {
        string.Empty, "discrete", "karras", "exponential", "ays", "gits", "sgm_uniform", "simple", "smoothstep", "kl_optimal", "lcm", "beta",
    };

    /// <summary>
    /// Семейство по имени файла или репозитория. null — не узнали (пользователь выберет сам).
    /// Порядок проверок важен: «qwen-image-edit» раньше «qwen-image», «kontext» раньше «flux».
    /// </summary>
    public static ImageFamily? Detect(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name!.ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        bool Has(params string[] parts) => parts.Any(p => n.Contains(p));

        // «qwen-image-2.1» раньше «qwen-image»; «qwen-image-2512» сюда не попадает
        if (Has("qwen-image-2.1", "qwen-image-21", "qwen-image-2-1", "qwenimage2.1", "qwenimage21"))
            return Has("turbo") ? ImageFamily.QwenImage21Turbo : ImageFamily.QwenImage21;
        if (Has("qwen-image-edit", "qwen-edit", "qwenimageedit"))
            return Has("2511") ? ImageFamily.QwenImageEdit2511 : ImageFamily.QwenImageEdit;
        if (Has("qwen-image", "qwenimage")) return ImageFamily.QwenImage;
        if (Has("z-image", "zimage")) return Has("turbo") ? ImageFamily.ZImageTurbo : ImageFamily.ZImage;
        if (Has("kontext")) return ImageFamily.FluxKontext;
        if (Has("flux.2", "flux2", "flux-2")) return Has("klein") ? ImageFamily.Flux2Klein : ImageFamily.Flux2;
        if (Has("flux")) return Has("schnell") ? ImageFamily.Flux1Schnell : ImageFamily.Flux1Dev;
        if (Has("chroma")) return ImageFamily.Chroma;
        if (Has("sd3", "sd-3", "stable-diffusion-3")) return ImageFamily.Sd3;
        if (Has("sdxl", "sd-xl", "-xl-", "xl-base", "stable-diffusion-xl", "pony", "illustrious"))
            return Has("turbo", "lightning", "hyper", "lcm") ? ImageFamily.SdxlTurbo : ImageFamily.Sdxl;
        if (Has("v2-1", "v2-0", "sd2", "sd-2", "stable-diffusion-2")) return ImageFamily.Sd2;
        if (Has("v1-5", "sd15", "sd-1.5", "sd1.5", "stable-diffusion-v1", "sd-v1")) return ImageFamily.Sd15;
        return null;
    }

    /// <summary>
    /// Роль файла по имени: mmproj → зрение LLM, «vae»/«ae.safetensors» → VAE, clip_l/clip_g/t5xxl — кодировщики,
    /// известные LLM-кодировщики (Qwen2.5-VL, Qwen3, Mistral) → --llm; остальное — основная модель
    /// (чекпойнт, если семейство хранит всё в одном файле).
    /// </summary>
    public static ImageComponent GuessComponent(string path, ImageFamily? family = null)
    {
        var f = Path.GetFileName(path).ToLowerInvariant().Replace('-', '_');
        if (f.Contains("mmproj")) return ImageComponent.LlmVision;
        // Маленькие автокодировщики: taesd, taesdxl, taef1, taew2_1, taeqi2_1, taehv — до проверки «vae»
        if (f.StartsWith("tae", StringComparison.Ordinal) || f.Contains("taesd") || f.Contains("_tae_") || f.Contains("taehv"))
            return ImageComponent.Tae;
        if (f.Contains("clip_vision")) return ImageComponent.ClipVision;
        if (f.Contains("clip_l")) return ImageComponent.ClipL;
        if (f.Contains("clip_g")) return ImageComponent.ClipG;
        if (f.Contains("t5xxl") || f.Contains("t5_v1_1") || f.Contains("umt5") || f.StartsWith("t5", StringComparison.Ordinal)) return ImageComponent.T5xxl;
        if (f.Contains("vae") || f is "ae.safetensors" or "ae.sft" || f.EndsWith("_ae.safetensors", StringComparison.Ordinal) || f.EndsWith("_ae.sft", StringComparison.Ordinal))
            return ImageComponent.Vae;
        if (f.Contains("qwen2.5_vl") || f.Contains("qwen_2.5_vl") || f.Contains("qwen2_5_vl") || f.Contains("qwen3_") || f.Contains("qwen_3_") ||
            f.Contains("qwen3vl") || f.Contains("qwen3.5_") ||
            f.Contains("mistral") || (f.Contains("instruct") && !f.Contains("image")))
            return ImageComponent.Llm;
        return family is { } fam && Get(fam).Required.Contains(ImageComponent.Checkpoint) ? ImageComponent.Checkpoint
             : family == null && Detect(path) is { } guessed && Get(guessed).Required.Contains(ImageComponent.Checkpoint) ? ImageComponent.Checkpoint
             : ImageComponent.DiffusionModel;
    }

    /// <summary>Файл весов, который понимает stable-diffusion.cpp.</summary>
    public static bool IsWeightFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gguf" or ".safetensors" or ".sft" or ".ckpt" or ".pth";
    }

    /// <summary>
    /// Часть репозитория в формате diffusers (unet/diffusion_pytorch_model…, text_encoder/model…): по отдельности
    /// stable-diffusion.cpp такие файлы не загружает — в списке скачивания они скрываются.
    /// </summary>
    public static bool IsDiffusersPart(string repoPath)
    {
        var p = repoPath.Replace('\\', '/').ToLowerInvariant();
        var slash = p.IndexOf('/');
        if (slash < 0) return false;
        var top = p.Substring(0, slash);
        var name = Path.GetFileName(p);
        return top is "unet" or "transformer" or "text_encoder" or "text_encoder_2" or "text_encoder_3" or "vae" or "vae_decoder" or "vae_encoder" or "safety_checker" or "image_encoder" or "controlnet"
               && (name.StartsWith("diffusion_pytorch_model", StringComparison.Ordinal) || name.StartsWith("model", StringComparison.Ordinal) || name.StartsWith("pytorch_model", StringComparison.Ordinal));
    }

    /// <summary>
    /// Размеры для пропорции при площади базового разрешения семейства (кратно 64 или кратности модели, если она 32).
    /// <paramref name="scale"/> — множитель стороны: 2 даёт «2K» (площадь ×4).
    /// </summary>
    public static (int width, int height) SizeFor(ImagePreset preset, int ratioW, int ratioH, double scale = 1.0)
    {
        var area = (double)preset.BaseArea * scale * scale;
        var w = Math.Sqrt(area * ratioW / ratioH);
        var h = area / w;
        var step = preset.RoundTo;
        int Round(double v) => Math.Max(256, (int)Math.Round(v / step) * step);
        return (Round(w), Round(h));
    }
}

/// <summary>Файл готового набора: роль, репозиторий Hugging Face, путь внутри и размер (проверены по API HF).</summary>
public sealed record ImageKitFile(ImageComponent Role, string Repo, string Path, long Size);

/// <summary>
/// Готовый набор «всё для запуска»: основная модель и все кодировщики одним нажатием.
/// <paramref name="MinRam"/> — сколько ОЗУ нужно, чтобы веса, выгруженные из видеопамяти, поместились без подкачки.
/// </summary>
public sealed record ImageKit(string Id, ImageFamily Family, string Title, IReadOnlyList<ImageKitFile> Files, long MinVram, bool Recommended = false, long MinRam = 0)
{
    public long TotalSize => Files.Sum(f => f.Size);
    public ImagePreset Preset => ImagePresets.Get(Family);
}

public static class ImageKits
{
    private const string ComfyAe = "split_files/vae/ae.safetensors";
    private const string ComfyZImage = "Comfy-Org/z_image_turbo";
    private const string QwenVae = "split_files/vae/qwen_image_vae.safetensors";
    private const string QwenComfy = "Comfy-Org/Qwen-Image_ComfyUI";
    private const string QwenVl = "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF";

    private static readonly ImageKitFile FluxVae = new(ImageComponent.Vae, ComfyZImage, ComfyAe, 335304388);
    private static readonly ImageKitFile QwenImageVae = new(ImageComponent.Vae, QwenComfy, QwenVae, 253806246);
    private static readonly ImageKitFile QwenVlLlm = new(ImageComponent.Llm, QwenVl, "Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf", 4683072512);

    // Qwen-Image 2.1 Turbo: DiT с imatrix (AD-кванты), Qwen3-VL-8B + mmproj, полный VAE (RGBA) и TAEQI2.1 для превью
    private const string Qwen21Turbo = "AtomicChat/Qwen-Image-2.1-Turbo-GGUF";
    private const string Qwen3Vl = "unsloth/Qwen3-VL-8B-Instruct-GGUF";
    private static readonly ImageKitFile Qwen21Vae = new(ImageComponent.Vae, "Comfy-Org/Qwen-Image-2.1", "vae/qwen_image_2.1_vae_bf16.safetensors", 675509688);
    private static readonly ImageKitFile Qwen21Tae = new(ImageComponent.Tae, "madebyollin/taeqi2_1", "taeqi2_1.safetensors", 30591264);
    private static readonly ImageKitFile Qwen3VlQ4 = new(ImageComponent.Llm, Qwen3Vl, "Qwen3-VL-8B-Instruct-UD-Q4_K_XL.gguf", 5148699488);
    private static readonly ImageKitFile Qwen3VlMmprojF16 = new(ImageComponent.LlmVision, Qwen3Vl, "mmproj-F16.gguf", 1159030336);
    private const long Gb = 1L << 30;

    /// <summary>Пути и размеры проверены по API Hugging Face (ноябрь 2026); закрытые (gated) репозитории не используются.</summary>
    public static IReadOnlyList<ImageKit> All { get; } = new[]
    {
        new ImageKit("z-image-turbo-q4", ImageFamily.ZImageTurbo, "Z-Image Turbo · Q4_K", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, "leejet/Z-Image-Turbo-GGUF", "z_image_turbo-Q4_K.gguf", 3864250304),
            FluxVae,
            new ImageKitFile(ImageComponent.Llm, "unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2497281120),
        }, 6 * Gb, Recommended: true),
        new ImageKit("sd15-q8", ImageFamily.Sd15, "Stable Diffusion 1.5 · Q8_0", new[]
        {
            new ImageKitFile(ImageComponent.Checkpoint, "second-state/stable-diffusion-v1-5-GGUF", "stable-diffusion-v1-5-pruned-emaonly-Q8_0.gguf", 1763578176),
        }, 2 * Gb),
        new ImageKit("sdxl-q4", ImageFamily.Sdxl, "SDXL base 1.0 · Q4_0", new[]
        {
            new ImageKitFile(ImageComponent.Checkpoint, "gpustack/stable-diffusion-xl-base-1.0-GGUF", "stable-diffusion-xl-base-1.0-Q4_0.gguf", 3940010720),
            new ImageKitFile(ImageComponent.Vae, "madebyollin/sdxl-vae-fp16-fix", "sdxl_vae.safetensors", 334641162),
        }, 6 * Gb),
        new ImageKit("flux1-schnell-q4", ImageFamily.Flux1Schnell, "FLUX.1 schnell · Q4_K", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, "leejet/FLUX.1-schnell-gguf", "flux1-schnell-q4_k.gguf", 6884606880),
            FluxVae,
            new ImageKitFile(ImageComponent.ClipL, "comfyanonymous/flux_text_encoders", "clip_l.safetensors", 246144152),
            new ImageKitFile(ImageComponent.T5xxl, "comfyanonymous/flux_text_encoders", "t5xxl_fp8_e4m3fn.safetensors", 4893934904),
        }, 8 * Gb),
        new ImageKit("qwen-image-2512-q3", ImageFamily.QwenImage, "Qwen-Image 2512 · Q3_K_M", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, "unsloth/Qwen-Image-2512-GGUF", "qwen-image-2512-Q3_K_M.gguf", 9012955680),
            QwenImageVae,
            QwenVlLlm,
        }, 10 * Gb),
        new ImageKit("qwen-image-edit-2509-q3", ImageFamily.QwenImageEdit, "Qwen-Image-Edit 2509 · Q3_K_S", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, "QuantStack/Qwen-Image-Edit-2509-GGUF", "Qwen-Image-Edit-2509-Q3_K_S.gguf", 9037543968),
            QwenImageVae,
            QwenVlLlm,
            new ImageKitFile(ImageComponent.LlmVision, QwenVl, "Qwen2.5-VL-7B-Instruct.mmproj-f16.gguf", 1354162912),
        }, 10 * Gb),
        // Универсальный для 8 ГБ: AD-Q4_K оставляет ~2,6 ГБ видеопамяти под внимание референсов (правка, 2K, LoRA)
        new ImageKit("qwen-image-21-turbo-q4", ImageFamily.QwenImage21Turbo, "Qwen-Image 2.1 Turbo · AD-Q4_K", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, Qwen21Turbo, "Qwen-Image-2.1-Turbo-AD-Q4_K.gguf", 4201694944),
            Qwen3VlQ4,
            Qwen3VlMmprojF16,
            Qwen21Vae,
            Qwen21Tae,
        }, 8 * Gb, MinRam: 16 * Gb),
        // Экономия ОЗУ: mmproj Q8_0 почти не отличается от F16 и на ~400 МБ меньше
        new ImageKit("qwen-image-21-turbo-q4-lite", ImageFamily.QwenImage21Turbo, "Qwen-Image 2.1 Turbo · AD-Q4_K · mmproj Q8_0", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, Qwen21Turbo, "Qwen-Image-2.1-Turbo-AD-Q4_K.gguf", 4201694944),
            Qwen3VlQ4,
            new ImageKitFile(ImageComponent.LlmVision, "Qwen/Qwen3-VL-8B-Instruct-GGUF", "mmproj-Qwen3VL-8B-Instruct-Q8_0.gguf", 752289728),
            Qwen21Vae,
            Qwen21Tae,
        }, 8 * Gb, MinRam: 16 * Gb),
        // Максимум качества в 1K: AD-Q5_K и кодировщик Q8_0 (мелкий текст); кодировщик удобно держать на CPU — нужно 32 ГБ ОЗУ
        new ImageKit("qwen-image-21-turbo-q5", ImageFamily.QwenImage21Turbo, "Qwen-Image 2.1 Turbo · AD-Q5_K · Q8_0", new[]
        {
            new ImageKitFile(ImageComponent.DiffusionModel, Qwen21Turbo, "Qwen-Image-2.1-Turbo-AD-Q5_K.gguf", 5399168736),
            new ImageKitFile(ImageComponent.Llm, Qwen3Vl, "Qwen3-VL-8B-Instruct-Q8_0.gguf", 8709520224),
            Qwen3VlMmprojF16,
            Qwen21Vae,
            Qwen21Tae,
        }, 8 * Gb, MinRam: 32 * Gb),
    };

    public static ImageKit? Find(string? id) => All.FirstOrDefault(k => k.Id == id);
}
