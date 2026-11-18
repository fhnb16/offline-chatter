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
    string? Recipe = null)
{
    public string Id => Family.ToString();

    /// <summary>Негативный промпт работает только при CFG > 1 (дистиллированные модели его игнорируют).</summary>
    public bool UsesNegativePrompt => Cfg > 1.0;

    /// <summary>Площадь базового разрешения — от неё считаются размеры для других пропорций.</summary>
    public int BaseArea => Width * Height;

    public IEnumerable<ImageComponent> AllComponents => Required.Concat(Optional);
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
        new ImagePreset(ImageFamily.ZImage, "Z-Image",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 20, 4.0, "euler",
            OffloadToCpu: true, Recipe: "z-image"),
        new ImagePreset(ImageFamily.ZImageTurbo, "Z-Image Turbo",
            new[] { ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.Llm }, None, 1024, 1024, 8, 1.0, "euler",
            OffloadToCpu: true, Recipe: "z-image-turbo"),
        new ImagePreset(ImageFamily.Custom, "Custom", None,
            new[] { ImageComponent.Checkpoint, ImageComponent.DiffusionModel, ImageComponent.Vae, ImageComponent.ClipL, ImageComponent.ClipG, ImageComponent.T5xxl, ImageComponent.Llm, ImageComponent.LlmVision },
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
        if (f.Contains("clip_vision")) return ImageComponent.ClipVision;
        if (f.Contains("clip_l")) return ImageComponent.ClipL;
        if (f.Contains("clip_g")) return ImageComponent.ClipG;
        if (f.Contains("t5xxl") || f.Contains("t5_v1_1") || f.Contains("umt5") || f.StartsWith("t5", StringComparison.Ordinal)) return ImageComponent.T5xxl;
        if (f.Contains("vae") || f is "ae.safetensors" or "ae.sft" || f.EndsWith("_ae.safetensors", StringComparison.Ordinal) || f.EndsWith("_ae.sft", StringComparison.Ordinal))
            return ImageComponent.Vae;
        if (f.Contains("qwen2.5_vl") || f.Contains("qwen_2.5_vl") || f.Contains("qwen2_5_vl") || f.Contains("qwen3_") || f.Contains("qwen_3_") ||
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

    /// <summary>Размеры для пропорции при площади базового разрешения семейства (кратно 64).</summary>
    public static (int width, int height) SizeFor(ImagePreset preset, int ratioW, int ratioH)
    {
        var area = (double)preset.BaseArea;
        var w = Math.Sqrt(area * ratioW / ratioH);
        var h = area / w;
        static int Round64(double v) => Math.Max(256, (int)Math.Round(v / 64.0) * 64);
        return (Round64(w), Round64(h));
    }
}

/// <summary>Файл готового набора: роль, репозиторий Hugging Face, путь внутри и размер (проверены по API HF).</summary>
public sealed record ImageKitFile(ImageComponent Role, string Repo, string Path, long Size);

/// <summary>Готовый набор «всё для запуска»: основная модель и все кодировщики одним нажатием.</summary>
public sealed record ImageKit(string Id, ImageFamily Family, string Title, IReadOnlyList<ImageKitFile> Files, long MinVram, bool Recommended = false)
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
    };

    public static ImageKit? Find(string? id) => All.FirstOrDefault(k => k.Id == id);
}
