# AGENTS.md — правила для ИИ-агентов и контрибьюторов

Прочитай этот файл целиком перед любыми изменениями. Цели продукта описаны в [README.md](README.md), план и текущий статус в [TODO.md](TODO.md).

## Жёсткие ограничения

1. **Целевая платформа: .NET Framework 4.8** (`net48`), WPF, C# `LangVersion=latest`. Не переводить проект на .NET Core / .NET 5+: они не работают на Win7.
2. **Windows 7 SP1 — первый класс поддержки.** Всё, что появилось после Win7, нужно:
   - вызывать только после проверки версии ОС (`OsInfo`) или через `GetProcAddress`;
   - обеспечить запасной вариант на случай, если функция недоступна.
   Это касается WinRT, `SetThreadDescription`, `DwmSetWindowAttribute` с новыми атрибутами, DXGI 1.4 и т. п.
3. **Вес.** Цель для exe со всеми зависимостями — 4–6 МБ. Новую NuGet-зависимость можно добавлять только если:
   - она поддерживает net48 / netstandard2.0;
   - она не тянет Microsoft.Extensions.\* и System.Text.Json;
   - в PR указан её вклад в размер.
   Разрешённый список см. ниже.
4. **Нельзя:** WebView2 / CefSharp / Electron-подобные компоненты, `AllowsTransparency=true` на главных окнах (это медленно), блокирующий I/O в UI-потоке.
5. **Git.** Агент не делает коммиты и push: репозиторием управляет владелец вручную.

## Разрешённые зависимости

| Пакет | Где | Зачем |
|---|---|---|
| Newtonsoft.Json | Core | JSON (конфиги, API, JSONL-логи) |
| CommunityToolkit.Mvvm | App | ObservableObject, RelayCommand |
| PolySharp | все | record / init / required на net48 (только на этапе компиляции) |
| Markdig | App/Core | Markdown |
| XAML-Math (`WpfMath`) | App | рендер LaTeX |
| AvalonEdit | App | подсветка кода, редактор JSON |
| ~~iNKORE.UI.WPF.Modern~~ | — | отклонён: собственный Fluent-скин легче |
| BouncyCastle.Cryptography | Core | резервный TLS (сжатым ресурсом) |
| Costura.Fody | App | склейка в один exe |
| xunit | tests | тесты |

## Архитектура

```
src/OfflineChatter.Core/      net48, БЕЗ ссылок на WPF (PresentationFramework/WindowsBase)
  Storage/      AppPaths, SettingsStore, ChatStore, Secrets
  Diagnostics/  Log (JSONL + ротация), CrashHandler, Benchmarks, Reports
  Net/          HttpFactory, TLS-уровни, CertPinning, Downloader
  Engine/       GitHubReleases, AssetMatcher, EngineInstaller, EngineProcess, FlagCatalog, HardwareProbe
  Models/       GgufReader, HfClient, QuantCatalog, MemoryEstimator, LocalScanner
  Providers/    IChatProvider, LlamaServerProvider, OpenAiCompatProvider, SseReader
  Mcp/          McpClient, BuiltinTools, AgentLoop
  Math/         MathAst + эмиттеры (MathML, OMML, PNG, LaTeX)
  Export/       Docx, Rtf(.doc), Html, Markdown
src/OfflineChatter/           WPF exe
  Interop/      P/Invoke (DWM, user32, uxtheme): только здесь
  Theming/      OsInfo, ThemeManager, Backdrops, AccentColors, WindowChromeHelper
  Skins/        словари ресурсов скинов
  Controls/  Views/  ViewModels/
tests/OfflineChatter.Core.Tests/
```

- **Core не зависит от WPF.** Всё, что можно протестировать без UI, кладётся в Core.
- Весь P/Invoke находится в `Interop/`, в классах `NativeMethods` по DLL. Для каждой функции указана минимальная версия ОС.
- MVVM: View ↔ ViewModel только через биндинги и команды. Code-behind допустим для чисто визуальной логики (WindowChrome, анимации).

## Темы и оформление

- `ThemeManager` — единая точка управления. Он отвечает за:
  - **скин** (системный словарь `PresentationFramework.{Aero|Aero2|AeroLite|Luna|Royale|Classic}` или Fluent);
  - **подложку** окна (None / AeroGlass / Win10Blur / Win10Acrylic / Mica / MicaAlt / Acrylic);
  - **акцент** (системный или свой);
  - **светлую / тёмную** тему.
- Цвета в XAML берутся только через `DynamicResource` с ключами из `ThemeKeys` (`Theming/ThemeTokens.cs`, там же значения для светлой/тёмной темы и каждой подложки). Литералы цветов в разметке запрещены.
- Каждая подложка обязана деградировать плавно: если ОС или режим не поддерживают эффект, используется сплошной фон. На Win7 также учитывается выключенная композиция DWM (Aero Basic / Classic) и событие `WM_DWMCOMPOSITIONCHANGED`.
- Окна с собственным заголовком на Win11 обязаны поддерживать Snap Layouts (`HTMAXBUTTON` в `WM_NCHITTEST`).
- Каждое окно подключается через `ThemeManager.Attach(window)` в конструкторе. Подложка применяется в `SourceInitialized` и **повторно в `ContentRendered`**: Win11 25H2 при первом показе сам выставляет тёмный заголовок по системной теме.
- `SetWindowCompositionAttribute` (Blur, Acrylic через SWCA) используется только на Win10. На Win11 с расширенной рамкой он даёт чёрный фон, там работает `DWMWA_SYSTEMBACKDROP_TYPE`.
## Хранилище и безопасность

- Пути берутся только из `AppPaths`, `Environment.CurrentDirectory` не используется. Портативный режим включается, если в папку с exe можно писать или рядом лежит `portable.flag`.
- JSON записывается атомарно: сначала `*.tmp`, затем `File.Replace` / `Move`.
- Секреты (API-ключи, токен HF) хранятся только через `Secrets` (DPAPI или мастер-пароль). В логах и диагностике они маскируются.
- Содержимое сообщений по умолчанию не логируется.
- `llama-server` слушает только `127.0.0.1` и запускается со случайным `--api-key`.
- Для каждого внешнего MCP-инструмента пользователь подтверждает вызов, если явно не разрешил его заранее.

## Стиль кода

- Nullable reference types включены (`<Nullable>enable</Nullable>`). Предупреждения не игнорировать.
- Async до самого верха. Везде, где есть I/O, передаётся `CancellationToken`. `.Result` / `.Wait()` в UI-потоке запрещены.
- В библиотеке Core используется `ConfigureAwait(false)`.
- Имена на английском. Комментарии на русском или английском, но только там, где без них непонятно, *почему* код написан именно так.
- Строки UI: добавить строку в `src/OfflineChatter/Localization/Strings.tsv` (ключ, EN, RU через табуляцию) и запустить `pwsh tools/gen-strings.ps1` — он пересоздаст `Strings.resx` и `StringsRu.resx` (вручную их не править). В XAML — `{l:Loc Key}`, в коде — `L.Get("Key")` / `L.F("Key", args)`. Строковые литералы в XAML не допускаются.
- Названия значений enum в UI: ключ `Enum_{Тип}_{Значение}` + конвертер `EnumDisplay`.

## Ручная проверка UI

```powershell
pwsh tools/screenshot.ps1 -Exe src/OfflineChatter/bin/Debug/net48/OfflineChatter.exe -Out shot.png `
     -Skin Aero -Mode Light -Backdrop Auto -Language en -Page Settings -HitTestMaxButton
```
Скрипт правит `config/settings.json` рядом с exe, запускает приложение (`--page` открывает раздел), снимает окно и закрывает его.

## Сборка и проверка

```powershell
dotnet build OfflineChatter.sln -c Debug
dotnet test  OfflineChatter.sln
```

Перед тем как считать задачу выполненной:
1. Сборка проходит без новых предупреждений.
2. Тесты Core зелёные. Новая логика в Core покрыта тестами.
3. При изменениях UI или тем сценарий проверен вручную хотя бы на текущей ОС, а в описании отмечено, что нужно проверить на Win7 / 8.1 / 10 / 11.
4. Если задача из [TODO.md](TODO.md), её чекбокс отмечен.
