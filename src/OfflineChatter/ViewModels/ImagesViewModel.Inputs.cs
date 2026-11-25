using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Images;
using OfflineChatter.Localization;
using OfflineChatter.Rendering;

namespace OfflineChatter.ViewModels;

/// <summary>Входные картинки страницы «Изображения»: референсы, исходник, маска, теги &lt;image:N&gt; и подготовка запроса.</summary>
public sealed partial class ImagesViewModel
{
    /// <summary>Больше референсов Qwen-Image 2.1 не принимает; на 8 ГБ и 4 — уже много.</summary>
    public const int MaxRefImages = 10;

    // ---------------- Референсы ----------------

    public ObservableCollection<RefImageItem> RefImages { get; } = new();

    public bool HasRefImages => RefImages.Count > 0;

    /// <summary>Лента референсов: в правке — всегда, в перерисовке — как дополнительные (если модель их понимает).</summary>
    public bool ShowRefs => Mode == ImageMode.Edit || (Mode == ImageMode.ImageToImage && CanEdit);

    /// <summary>«Референсы уменьшены до 1536 px для защиты видеопамяти» — или null.</summary>
    [ObservableProperty] private string? _refScaleHint;

    private void OnRefsChanged()
    {
        for (var i = 0; i < RefImages.Count; i++) RefImages[i].Index = i + 1;
        OnPropertyChanged(nameof(HasRefImages));
        OnPropertyChanged(nameof(PromptTagsText));
        ApplySizeFromSource();
        UpdateRefInfo();
        GenerateCommand.NotifyCanExecuteChanged();
        Final2KCommand.NotifyCanExecuteChanged();
    }

    private int RefLimit() => ImageInputPolicy.RefMaxSide(RefImages.Count, nativeSize: Is2K || Math.Max(Width, Height) >= 1536);

    private int InputMultiple() => Preset is { SizeMultiple: >= 32 } p ? p.SizeMultiple : 1;

    /// <summary>Подписи «→ 1536 × 1152» у референсов, которые будут уменьшены, и общая подсказка.</summary>
    private void UpdateRefInfo()
    {
        var limit = RefLimit();
        var multiple = InputMultiple();
        var any = false;
        foreach (var r in RefImages)
        {
            if (!LimitInputSize)
            {
                r.SentText = null;
                continue;
            }
            var (w, h, changed) = ImageInputPolicy.FitWithin(r.Image.Width, r.Image.Height, limit, multiple);
            r.SentText = changed ? L.F("Img_RefSent", w, h) : null;
            any |= changed;
        }
        RefScaleHint = any ? L.F("Img_RefsScaled", limit) : null;
    }

    [RelayCommand]
    private void RemoveRef(RefImageItem? item)
    {
        if (item != null) RefImages.Remove(item);
    }

    /// <summary>Сделать референс первым: по первому считается размер, и он — «основная» картинка правки.</summary>
    [RelayCommand]
    private void MakeFirstRef(RefImageItem? item)
    {
        if (item == null) return;
        var i = RefImages.IndexOf(item);
        if (i > 0) RefImages.Move(i, 0);
    }

    // ---------------- Картинки внутри промпта ----------------

    /// <summary>Вставить &lt;image:N&gt; в конец промпта (по щелчку на номер референса).</summary>
    [RelayCommand]
    private void InsertTag(RefImageItem? item)
    {
        if (item == null) return;
        var p = Prompt.TrimEnd();
        Prompt = p + (p.Length > 0 ? " " : string.Empty) + item.Tag + " ";
    }

    /// <summary>«В промпте: картинки 1, 2» — что уйдёт в prompt_images.</summary>
    public string? PromptTagsText
    {
        get
        {
            var ids = PromptImageTags.Ids(Prompt);
            return ids.Count == 0 ? null : L.F("Img_TagsInPrompt", string.Join(", ", ids));
        }
    }

    /// <summary>Теги, которые не на что сопоставить, или модель без зрения: ошибка до запуска (sd.cpp упал бы на кодировании).</summary>
    private string? CheckPromptTags()
    {
        var ids = PromptImageTags.Ids(Prompt);
        if (ids.Count == 0) return null;
        if (Preset?.SupportsPromptImages != true) return L.Get("Img_TagsUnsupported");
        if (Mode == ImageMode.TextToImage) return L.Get("Img_TagsNeedImages");
        foreach (var id in ids)
        {
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > RefImages.Count)
                return L.F("Img_TagUnknown", PromptImageTags.Tag(id));
        }
        return null;
    }

    // ---------------- Исходник, маска, подгонка ----------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(Final2KCommand))]
    [NotifyPropertyChangedFor(nameof(HasInit), nameof(InitPixelSize), nameof(InitPreview))]
    private RefImageItem? _initImage;

    private BitmapSource? _initPreview;

    public bool HasInit => InitImage != null;

    /// <summary>Исходник для холста маски (до 1024 по ширине — штрихи всё равно в пикселях оригинала).</summary>
    public BitmapSource? InitPreview => _initPreview;

    public Size InitPixelSize => InitImage == null ? Size.Empty : new Size(InitImage.Image.Width, InitImage.Image.Height);

    partial void OnInitImageChanging(RefImageItem? value)
    {
        _initPreview = value == null ? null : ImagePrep.Thumbnail(value.Data, Math.Min(1024, value.Image.Width));
    }

    partial void OnInitImageChanged(RefImageItem? value)
    {
        MaskPng = null;
        ApplySizeFromSource();
    }

    [RelayCommand]
    private void ClearInit() => InitImage = null;

    /// <summary>Маска (белое — перерисовать) в пикселях исходника; null — перерисовывается всё.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMask))]
    private byte[]? _maskPng;

    public bool HasMask => MaskPng != null;

    [ObservableProperty] private bool _maskInvert;

    /// <summary>Диаметр кисти — доля длинной стороны исходника.</summary>
    [ObservableProperty] private double _brushFraction = 0.05;

    [ObservableProperty] private bool _isEraser;

    [RelayCommand]
    private void ClearMask() => MaskPng = null;

    [ObservableProperty] private ImageFit _initFit = ImageFit.CropResize;

    public IReadOnlyList<ImageFit> Fits { get; } = Enum.GetValues(typeof(ImageFit)).Cast<ImageFit>().ToList();

    /// <summary>Отправить контуры исходника вместо него самого: композиция по линиям, цвета и фактура — заново.</summary>
    [ObservableProperty] private bool _initCanny;

    // ---------------- Добавление картинок ----------------

    /// <summary>Куда идёт новая картинка: в перерисовке — исходник (если его нет), иначе — референс.</summary>
    private void AddImage(GenImage img, string name, bool forceRef = false)
    {
        var item = RefImageItem.Create(img, name);
        if (!forceRef && Mode == ImageMode.ImageToImage && (InitImage == null || !CanEdit))
        {
            InitImage = item;
            return;
        }
        if (Mode == ImageMode.TextToImage)
        {
            if (CanEdit) Mode = ImageMode.Edit;
            else if (Modes.Contains(ImageMode.ImageToImage))
            {
                Mode = ImageMode.ImageToImage;
                InitImage = item;
                return;
            }
            else return;
        }
        if (RefImages.Count >= MaxRefImages)
        {
            Error = L.F("Img_TooManyRefs", MaxRefImages);
            return;
        }
        RefImages.Add(item);
    }

    /// <summary>Файлы (перетаскивание, выбор, «продолжить правку»): читаются в фоне; параметры PNG программы можно восстановить.</summary>
    public Task AddFilesAsync(IEnumerable<string> paths, bool restoreParams = true) => AddFilesCoreAsync(paths.ToList(), restoreParams, forceRef: false);

    private async Task AddFilesCoreAsync(IReadOnlyList<string> paths, bool restoreParams, bool forceRef)
    {
        foreach (var path in paths)
        {
            try
            {
                var (img, sidecar) = await Task.Run(() =>
                {
                    var image = ImagePrep.FromFile(path);
                    var meta = restoreParams && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ReadSidecar(path) : null;
                    return (image, meta);
                });
                AddImage(img, Path.GetFileName(path), forceRef);
                if (sidecar?.Params is { } p) DroppedParams = p;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Error = L.F("Img_CannotOpen", Path.GetFileName(path), ex.Message);
            }
        }
    }

    private const string ImageFilterExt = "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif";

    [RelayCommand]
    private void PickImages()
    {
        var init = Mode == ImageMode.ImageToImage;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get(init ? "Img_PickInit" : "Img_PickRefs"),
            Filter = L.Get("Img_ImageFilter") + ImageFilterExt,
            Multiselect = !init,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        // Новый исходник заменяет старый
        if (init) InitImage = null;
        _ = AddFilesCoreAsync(dlg.FileNames, restoreParams: true, forceRef: false);
    }

    /// <summary>Добавить референсы и в режиме перерисовки (как дополнительные).</summary>
    [RelayCommand]
    private void PickRefs()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get("Img_PickRefs"),
            Filter = L.Get("Img_ImageFilter") + ImageFilterExt,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) _ = AddFilesCoreAsync(dlg.FileNames, restoreParams: false, forceRef: true);
    }

    /// <summary>Вставить картинку из буфера (Ctrl+V на странице): без потерь, с прозрачностью.</summary>
    [RelayCommand]
    public void PasteImage()
    {
        var bmp = ImageAttachments.GetClipboardImage();
        if (bmp == null)
        {
            if (Clipboard.ContainsFileDropList()) _ = AddFilesAsync(Clipboard.GetFileDropList().Cast<string>());
            return;
        }
        if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
        _ = PasteAsync(bmp);
    }

    private async Task PasteAsync(BitmapSource bmp)
    {
        try
        {
            var img = await Task.Run(() => ImagePrep.FromBitmap(bmp));
            AddImage(img, "clipboard.png");
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or IOException)
        {
            Error = L.F("Img_CannotOpen", "clipboard", ex.Message);
        }
    }

    // ---------------- Подготовка запроса ----------------

    /// <summary>Что взять из страницы (в UI-потоке); тяжёлая подготовка — в <see cref="BuildRequest"/> в фоне.</summary>
    private sealed record InputSnapshot(
        IReadOnlyList<GenImage> Refs,
        GenImage? Init,
        byte[]? Mask,
        bool LimitSize,
        int RefLimit,
        int Multiple,
        bool AutoTiling,
        int TileSize,
        BitmapSource? BeforeImage);

    private InputSnapshot SnapshotInputs(ImageGenParams p)
    {
        var useRefs = p.Mode == ImageMode.Edit || (p.Mode == ImageMode.ImageToImage && CanEdit);
        var refs = useRefs ? RefImages.Select(r => r.Image).ToList() : new List<GenImage>();
        var init = p.Mode == ImageMode.ImageToImage ? InitImage?.Image : null;
        var settings = _s.Settings.Current.Images;
        var before = p.Mode switch
        {
            ImageMode.ImageToImage => InitPreview,
            ImageMode.Edit when RefImages.Count > 0 => ImagePrep.Thumbnail(RefImages[0].Data, Math.Min(1024, RefImages[0].Image.Width)),
            _ => null,
        };
        return new InputSnapshot(
            refs,
            init,
            init != null ? MaskPng : null,
            LimitInputSize,
            ImageInputPolicy.RefMaxSide(refs.Count, nativeSize: Math.Max(p.Width, p.Height) >= 1536),
            InputMultiple(),
            settings.AutoVaeTiling,
            Math.Max(64, settings.VaeTileSize),
            before);
    }

    /// <summary>
    /// Готовый запрос: референсы уменьшены по правилам видеопамяти, исходник и маска подогнаны под холст (маска — с порогом,
    /// без полутонов), контуры — по желанию, теги &lt;image:N&gt; сопоставлены с референсами, плитки VAE — по размерам.
    /// </summary>
    private static ImageRequest BuildRequest(ImageGenParams p, InputSnapshot s)
    {
        var refs = s.Refs.Select(r => s.LimitSize ? ImagePrep.Downscale(r, s.RefLimit, s.Multiple) : r).ToList();
        var promptImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var id in PromptImageTags.Ids(p.Prompt))
        {
            if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= refs.Count)
                promptImages[id] = refs[n - 1].Data;
        }

        byte[]? init = null, mask = null;
        if (s.Init != null)
        {
            init = p.InitCanny ? ImagePrep.CannyOnCanvas(s.Init, p.Width, p.Height, p.InitFit) : ImagePrep.FitToCanvas(s.Init, p.Width, p.Height, p.InitFit);
            if (s.Mask != null && ImagePrep.MaskHasInk(s.Mask)) mask = ImagePrep.MaskOnCanvas(s.Mask, p.Width, p.Height, p.InitFit, p.MaskInvert);
        }
        var tiling = s.AutoTiling && ImageInputPolicy.NeedsVaeTiling(p.Width, p.Height, refs.Select(r => (r.Width, r.Height)));
        return new ImageRequest
        {
            Params = p,
            RefImages = refs.Select(r => r.Data).ToList(),
            InitImage = init,
            MaskImage = mask,
            PromptImages = promptImages,
            VaeTiling = tiling,
            VaeTileSize = s.TileSize,
        };
    }
}
