# TODO

Полный план с обоснованием решений описан в разделах ниже. Правила для разработчиков и агентов — в [AGENTS.md](AGENTS.md).

## Этап 0 — Репозиторий
- [x] .gitignore
- [x] README.md
- [x] AGENTS.md
- [x] TODO.md

## Этап 1 — Каркас
- [x] Решение: `OfflineChatter.Core` (net48), `OfflineChatter` (WPF, net48), `OfflineChatter.Core.Tests`
- [x] `app.manifest`: supportedOS Win7–11, PerMonitorV2, longPathAware. В `App.config`: `supportedRuntime` 4.8 и переключатели DPI
- [x] `AppPaths`: портативный режим или `%LOCALAPPDATA%`, `portable.flag`, проверка права записи
- [x] `SettingsStore`: JSON, атомарная запись, значения по умолчанию, восстановление испорченного файла
- [x] `Log`: JSONL с ротацией по размеру, уровни, фоновая запись. `CrashHandler`
- [x] `OsInfo`: определение 7 / 8 / 8.1 / 10 / 11 и номера сборки (через RtlGetVersion)
- [x] `ThemeManager`: системные скины, тёмная тема, акцент (DWM colorization / реестр / свой)
- [x] Подложки окна: Aero Glass (Win7), Blur и Acrylic (Win10), Mica / MicaAlt / Acrylic (Win11 22H2+), Mica на 21H2
- [x] Тёмный заголовок через DWM (атрибуты 19 и 20). Win11 25H2 перезаписывает его при первом показе окна, поэтому тема применяется повторно в `ContentRendered`
- [x] Главное окно: боковая панель, область чата, панель ввода, строка состояния, правая панель «Оформление и сведения»
- [x] Собственный минимальный Fluent-скин (`Skins/Fluent.xaml`): Button, TextBox, ComboBox, ListBox, CheckBox, ScrollBar, ToolTip
- [x] Обход лагов Acrylic на Win10: на время перетаскивания включается Blur (`WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE`). **Не проверено на Win10**
- [ ] Свой заголовок окна через WindowChrome и Snap Layouts на Win11 (`HTMAXBUTTON`)
- [ ] Fluent-скин на основе iNKORE.UI.WPF.Modern (ленивая загрузка): заменит или дополнит собственный
- [ ] Свечение текста на стекле Win7
- [ ] Локализация: `Strings.resx` / `Strings.ru.resx` (строки сейчас помечены `TODO(l10n)`)
- [ ] Склейка в один exe (Costura.Fody), замер веса

## Этап 2 — Сеть
- [ ] `HttpFactory`: TLS 1.2, прокси (системный или ручной), User-Agent, таймауты
- [ ] Уровень 2: вшитые корневые сертификаты (`X509Chain.ExtraStore`, сверка отпечатка корня, revocation с таймаутом)
- [ ] Уровень 3: BouncyCastle TLS + свой HTTP/1.1-обработчик (chunked, redirect, Range, CONNECT)
- [ ] Автоматический выбор уровня по первой ошибке, запоминание для каждого хоста, кнопка «Проверить сеть»
- [ ] `Downloader`: очередь, `.part` и Range, пауза, лимит скорости, проверка свободного места, sha256, прогресс в панели задач
- [ ] Зеркала: HF endpoint, GitHub endpoint

## Этап 3 — Движок llama.cpp
- [ ] `GitHubReleases`: список релизов, закреплённый тег, latest, поле `digest`
- [ ] `AssetMatcher` (regex): cpu / vulkan / cuda-12 / cuda-13 + cudart / hip / sycl / arm64
- [ ] `HardwareProbe`: CPU (ядра, AVX), RAM, GPU (DXGI / WMI), NVML (версия драйвера и CUDA)
- [ ] Фильтр бэкендов по ОС (на Win7 только CPU и Vulkan)
- [ ] `EngineInstaller`: распаковка в `engine/<tag>-<backend>/`, несколько версий рядом
- [ ] `CompatChecker`: UCRT, VC++, smoke-тест `--version`, диагностика кода `0xC0000139`
- [ ] `FlagCatalog`: разбор `llama-server --help`
- [ ] `EngineProcess`: Job Object KILL_ON_JOB_CLOSE, при неудаче breakaway и PID-файл, очистка «сирот» при старте
- [ ] Только 127.0.0.1, случайный `--api-key`, автоматический порт, опрос `/health`, прогресс загрузки модели из stdout
- [ ] Использование API сервера: `/props`, `/tokenize`, `/slots` (сохранение и восстановление KV для каждого чата), `/metrics`
- [ ] Router mode (если версия его поддерживает)

## Этап 4 — Модели и Hugging Face
- [ ] `GgufReader`: метаданные из локального файла и удалённо через HTTP Range
- [ ] `MemoryEstimator`: веса + KV + буфер. Использовать `--fit`, если версия его поддерживает
- [ ] `HfClient`: поиск (`filter=gguf`, `expand[]=gguf`, vision), `tree?recursive=true`, шарды, mmproj, gated-модели, токен
- [ ] `QuantCatalog`: порядок квантов, описания, рекомендация под железо
- [ ] `LocalScanner`: свои папки, кэш HF, LM Studio
- [ ] UI ModelHub: поиск, карточка модели (README), выбор кванта, очередь загрузок
- [ ] Модель по умолчанию `NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF`

## Этап 5 — Конфигурация запуска
- [ ] Профили «Быстро / Баланс / Качество» и пользовательские, привязка профиля к модели
- [ ] Расширенная панель: группы, поиск по флагам, подсказки из `--help`, произвольные аргументы
- [ ] Живое превью командной строки, «скопировать как .bat», сравнение профилей (diff)
- [ ] Проверка «влезет ли модель» до запуска

## Этап 6 — Чат
- [ ] `IChatProvider`, `SseReader`, `LlamaServerProvider`, `OpenAiCompatProvider`
- [ ] Таблица quirks провайдеров, пресеты (OpenAI, Gemini, OpenRouter, Groq, DeepSeek, Mistral, Anthropic-compat, LM Studio, Ollama, vLLM)
- [ ] Инкрементальный Markdown-рендер (замороженные блоки, батчинг около 30 fps)
- [ ] Формулы: `$`, `$$`, `\(` и `\[`, эвристика для `$` как знака валюты, запасной вариант с исходным LaTeX
- [ ] Блоки кода с подсветкой и кнопкой копирования, сворачиваемые `<think>` с таймером
- [ ] Вложения: изображения (drag-n-drop, Ctrl+V, уменьшение), текст, DOCX, PDF (опционально)
- [ ] Ветвление ответов, перегенерация, редактирование, стоп
- [ ] Управление контекстом: индикатор заполнения, стратегия при переполнении
- [ ] Системные промпты и персоны, библиотека промптов, параметры для отдельного чата, переключатель thinking
- [ ] `ChatStore`: JSON на каждый чат, поиск, закрепление, папки и теги
- [ ] Цветные эмодзи (Emoji.Wpf или свой рендер)

## Этап 7 — Статистика, бенчмарки, логи
- [ ] Живая панель: TTFT, скорость промпта и генерации, спарклайн, draft acceptance, контекст, CPU / RAM / VRAM
- [ ] Метрики для удалённых API (время чанков, `usage`)
- [ ] `llama-bench -o json` и chat-bench (N прогонов, медиана, p95)
- [ ] Отчёты со снимком системы, сравнение, экспорт HTML / MD / CSV / JSON
- [ ] Просмотрщик логов с фильтрами, лог движка
- [ ] «Собрать диагностику» (zip без секретов)

## Этап 8 — Формулы и экспорт
- [ ] Math AST на основе парсера XAML-Math
- [ ] Эмиттеры: MathML, OMML, PNG, LaTeX
- [ ] `DocxWriter` (System.IO.Packaging)
- [ ] `RtfWriter`: .rtf и .doc (97–2003), формулы как PNG + WMF
- [ ] `HtmlWriter`: самодостаточная страница, MathML, опционально KaTeX, светлая и тёмная тема
- [ ] `MarkdownWriter`, экспорт и импорт JSON, XPS / печать
- [ ] Экспорт сообщения, выделенных сообщений, всего чата

## Этап 9 — MCP и инструменты
- [ ] `McpClient`: JSON-RPC 2.0, stdio, Streamable HTTP, legacy SSE
- [ ] Поддержка tools, resources, prompts, progress, `list_changed`
- [ ] `config/mcp.json` в формате `mcpServers` (совместим с Claude Desktop)
- [ ] Встроенные инструменты: калькулятор (рациональная арифметика), выражения, единицы измерения, дата и время, файлы из песочницы, fetch, веб-поиск
- [ ] Согласие на вызов (каждый раз / в этом чате / всегда), карточки вызовов, лимит шагов агентного цикла

## Этап 10 — Полировка
- [ ] Мастер первого запуска (железо → бэкенд → движок → модель → тестовое сообщение)
- [ ] Палитра команд (Ctrl+K), поиск в чате (Ctrl+F), горячие клавиши
- [ ] Окно быстрого вопроса по глобальной горячей клавише, трей, Jump List, уведомления
- [ ] Режим сравнения двух моделей
- [ ] Озвучивание (System.Speech), режим raw completion
- [ ] `Secrets`: DPAPI и мастер-пароль для портативного режима
- [ ] Самообновление с проверкой подписи

## Этап 11 — Совместимая сборка для Win7 (по необходимости)
- [ ] GitHub Actions: llama.cpp с `_WIN32_WINNT=0x0601`, CPU и Vulkan, публикация отдельным каналом

## Риски для проверки на реальном железе
- [ ] Запускаются ли официальные сборки llama.cpp (CPU и Vulkan) на Win7 SP1
- [ ] Согласует ли SChannel на Win7 шифры с GitHub, release-assets.githubusercontent, HF cas-bridge и Cloudflare
- [ ] Какие конструкции LLM-LaTeX покрывает XAML-Math (`align`, `cases`, `pmatrix`, `\text`, `\operatorname`)
- [ ] Насколько тормозит Acrylic на Win10, как ведёт себя Mica на Win11 21H2 по сравнению с 22H2+
- [ ] Blur и Acrylic через SWCA с `DwmExtendFrameIntoClientArea(-1)` на Win10: нормальный фон или чёрный (на Win11 26200 чёрный, поэтому там SWCA отключён)
- [ ] Aero Glass на Win7: читаемость боковой панели, переключение композиции (Aero ↔ Aero Basic) на лету
- [x] Проверено на Win11 26200: Fluent тёмная + Mica / Acrylic, Fluent светлая + Mica, Aero + Mica Alt, Classic без подложки
- [ ] Импорт .doc (RTF) в Word 97/2003, LibreOffice; формулы OMML в Word 2007/2010

## Матрица ручного тестирования
- [ ] Win7 SP1 без обновлений (сертификаты, TLS-резерв)
- [ ] Win7 SP1 с обновлениями (Aero, Aero Basic, Classic)
- [ ] Win8.1
- [ ] Win10 22H2 (светлая и тёмная тема, Acrylic)
- [ ] Win11 23H2+ (Mica, Mica Alt, Acrylic)
- [ ] ARM64 (по возможности)
