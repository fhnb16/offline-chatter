# Offline Chatter

Лёгкая нативная оболочка для [llama.cpp](https://github.com/ggml-org/llama.cpp) и OpenAI-совместимых API — один exe (~6 МБ) на .NET Framework 4.8.
Работает на Windows 7 SP1 и новее и на каждой версии выглядит как родное приложение:
Aero Glass на Win7, Aero2 на 8/8.1, Fluent с Acrylic на Win10, Mica / Mica Alt / Acrylic на Win11.

> Статус и оставшиеся задачи — [TODO.md](TODO.md).

## Возможности

- **Первый запуск** — мастер: определяет железо, ставит llama.cpp (CPU по умолчанию, GPU-сборка опцией) и модель по умолчанию одной кнопкой.
- **Движок** — релизы с GitHub с выбором версии и бэкенда (CPU, Vulkan, CUDA 12/13 + cudart, ROCm, SYCL, OpenVINO, ARM64), фильтр по ОС и драйверу, проверка SHA-256, распаковка в `engine/`, самопроверка и диагностика (нет CRT, слишком старая ОС, не те инструкции CPU). Можно подключить **свою папку** с llama.cpp. Канал «Win7-compat» — сборки из CI этого репозитория.
- **Профили запуска** — «Быстро / Баланс / Качество» и свои: контекст, слои на GPU (`--fit` или своя оценка VRAM), потоки, батчи, KV-кэш, flash-attn, черновая модель, произвольные аргументы; превью командной строки, «скопировать как .bat», сравнение профилей, оценка «влезет ли».
- **Модели** — поиск GGUF на Hugging Face (фильтры «vision», «влезет в мою память»), выбор кванта с оценкой ОЗУ/VRAM (метаданные GGUF читаются удалённо до скачивания), шарды, автоматический mmproj, gated-модели с токеном, докачка, зеркала. **Свой GGUF с любого диска** без копирования; поиск уже скачанного (кэш HF, LM Studio). Модель по умолчанию: [NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF](https://huggingface.co/NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF).
- **Чат** — стриминг, Markdown, формулы LaTeX (`$`, `$$`, `\(`, `\[`), подсветка кода, таблицы; сворачиваемые рассуждения с таймером и режим размышлений; изображения (кнопка, перетаскивание, Ctrl+V), текстовые файлы и DOCX; ветвление (правка, варианты ответа ‹ n/m ›), стоп, системный промпт; живая статистика и индикатор контекста; озвучивание.
- **API** — OpenAI, Gemini, OpenRouter, Anthropic (OpenAI-compat), DeepSeek, Groq, Mistral, LM Studio, Ollama, vLLM, удалённый llama.cpp, любой свой; учёт особенностей каждого API.
- **Инструменты** — встроенные (точный калькулятор, единицы, дата и время, файлы из песочницы, загрузка страниц, поиск SearXNG) и **MCP-серверы** (stdio, Streamable HTTP, SSE; формат `mcpServers` как у Claude Desktop, импорт оттуда); подтверждение вызовов, агентный цикл.
- **Сравнение моделей** — один промпт двум моделям рядом.
- **Бенчмарки** — `llama-bench` и chat-bench (медиана, p95), отчёты со снимком системы, сравнение, экспорт HTML/MD/CSV/JSON.
- **Экспорт** — DOCX (формулы Word — OMML), DOC (Word 97–2003) и RTF, HTML (MathML), Markdown, JSON, печать.
- **Сеть на старых системах** — TLS 1.2 на Win7; при ошибке — вшитые корневые сертификаты; затем собственный TLS 1.2/1.3 (BouncyCastle). Прокси, зеркала, «Проверить сеть».
- **Журнал и диагностика** — JSONL с ротацией, просмотрщик, ресурсы llama-server (CPU, ОЗУ, VRAM), архив диагностики без секретов.
- **Удобство** — палитра команд (Ctrl+K), быстрый вопрос (Ctrl+Alt+Space), трей, Jump List, RU/EN, светлая/тёмная тема, свой акцентный цвет, High Contrast, Per-Monitor DPI, ключи через DPAPI или мастер-пароль (для переноса портативной папки).

## Требования

| | |
|---|---|
| ОС | Windows 7 SP1 / 8.1 / 10 / 11, x64 или ARM64 (на x86 работают только удалённые API) |
| Рантайм | .NET Framework 4.8. Уже есть в Win10 1903+ и Win11. На Win7 для установки 4.8 нужен KB4474419 (поддержка SHA-2) |
| Движок на Win7 | UCRT (KB2999226) и VC++ 2015–2022 x64 — программа проверяет сама; сборки канала Win7-compat от VC++ не зависят |

## Сборка

Нужен .NET SDK 8+ (подойдёт и новее). Developer Pack для 4.8 не обязателен — reference assemblies приходят из NuGet.

```powershell
dotnet build OfflineChatter.sln -c Release   # → src/OfflineChatter/bin/Release/net48/OfflineChatter.exe (один файл)
dotnet test  OfflineChatter.sln              # офлайн-тесты Core
$env:OC_NET_TESTS=1; dotnet test             # + сетевые (GitHub, Hugging Face, все уровни TLS)
$env:OC_E2E=1; dotnet test                   # + сквозной: установка llama.cpp, модель, сервер, ответ
```

Строки интерфейса правятся в `src/OfflineChatter/Localization/Strings.tsv`, затем `pwsh tools/gen-strings.ps1`.

## Командная строка

| Ключ | Действие |
|---|---|
| `--page Models` | открыть раздел (Chat, Models, Engine, Providers, Mcp, Compare, Benchmarks, Logs, Settings, Welcome) |
| `--prompt "текст"` | новый чат и сразу отправить сообщение |
| `--new`, `--chat <id>`, `--quick` | новый чат, открыть чат, быстрый вопрос |

Повторный запуск передаёт ключи уже открытому окну.

## Папки рядом с exe

```
OfflineChatter.exe
engine/<tag>-<backend>/   сборки llama.cpp (+ help.txt, engine.json)
config/                   settings.json, profiles/, providers.json, mcp.json, models.json, certs/
models/                   GGUF (папка настраивается; свои модели могут лежать где угодно)
chats/                    чаты (JSON на каждый)
reports/                  отчёты бенчмарков
logs/                     app*.jsonl, engine*.jsonl, crash-*.log
cache/                    загрузки, KV-слоты, кэш --help
```

Если в папку с exe нельзя писать (например, `Program Files`), данные хранятся в `%LOCALAPPDATA%\OfflineChatter`.
Файл `portable.flag` рядом с exe принудительно включает портативный режим.

## Исходники

```
src/OfflineChatter.Core/   без WPF: Net, Engine, Models, Chat, Mcp, Tools, Formulas, Export, Benchmarks, Storage, Diagnostics
src/OfflineChatter/        WPF: Theming, Skins, Rendering (Markdown, формулы), Views, ViewModels, Shell (трей, горячие клавиши)
tests/                     xUnit-тесты Core
tools/                     gen-strings.ps1, make-icon.ps1, screenshot.ps1
.github/workflows/         сборка приложения; сборка llama.cpp для Win7
```

Правила для разработчиков и ИИ-агентов — [AGENTS.md](AGENTS.md).

## Лицензия

[Apache 2.0](LICENSE)
