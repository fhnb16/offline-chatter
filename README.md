# Offline Chatter

Лёгкая нативная оболочка для [llama.cpp](https://github.com/ggml-org/llama.cpp) и OpenAI-совместимых API.
Работает на Windows 7 SP1 и новее и на каждой версии выглядит как родное приложение:
Aero Glass на Win7, Aero2 на 8/8.1, Fluent с Acrylic на Win10, Mica / Mica Alt / Acrylic на Win11.

> Статус: ранняя разработка. План работ лежит в [TODO.md](TODO.md).

## Возможности (целевые)

- **Движок.** Скачивает `llama-server` из GitHub Releases с выбором версии и бэкенда (CPU по умолчанию, Vulkan, CUDA, HIP, SYCL, ARM64). Распаковывает в `engine/` рядом с exe, проверяет совместимость с ОС и запускает с настройками пользователя.
- **Модели.** Поиск GGUF на Hugging Face с фильтрами («влезет в мою память», vision, размер). Выбор кванта с оценкой расхода RAM/VRAM, автоматическая загрузка `mmproj` для vision-моделей. Докачка и проверка SHA256, импорт уже скачанных моделей. Модель по умолчанию: [NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF](https://huggingface.co/NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF).
- **Чат.** Потоковый вывод, Markdown, формулы LaTeX, подсветка кода, изображения (vision). Сворачиваемые блоки `<think>`, ветвление ответов, режим сравнения двух моделей.
- **Удалённые API.** OpenAI, Gemini, OpenRouter, DeepSeek, Groq, LM Studio, Ollama и любой OpenAI-совместимый сервер.
- **MCP.** Встроенные инструменты (калькулятор, единицы измерения, дата и время) и подключение внешних MCP-серверов (stdio / HTTP) в формате `mcpServers`.
- **Статистика.** Время до первого токена, скорость обработки промпта и генерации, заполнение контекста, RAM/VRAM. Бенчмарки (`llama-bench` и chat-bench) с отчётами и сравнением, журналы в формате JSONL.
- **Экспорт.** `.docx`, `.doc` (97–2003, через RTF), `.rtf`, HTML (MathML / KaTeX), Markdown, JSON.
- **Сеть на старых системах.** Принудительный TLS 1.2. Если не помогает, используются вшитые корневые сертификаты, а в крайнем случае собственный TLS-стек (BouncyCastle). Можно указать зеркала и прокси.
- **Оформление.** Темы Авто / Aero / Aero2 / AeroLite / Luna / Royale / Classic / Fluent, светлая и тёмная, системный или свой акцентный цвет, Per-Monitor DPI, High Contrast.

## Требования

| | |
|---|---|
| ОС | Windows 7 SP1 / 8.1 / 10 / 11, x64 или ARM64 (на x86 работают только удалённые API) |
| Рантайм | .NET Framework 4.8. Уже встроен в Win10 1903+ и Win11. На Win7 для установки 4.8 нужен KB4474419 (поддержка SHA-2) |
| Движок на Win7 | UCRT (KB2999226) и VC++ 2015–2022 x64. Приложение проверяет их само |

## Сборка

Нужен .NET SDK 6+ (подойдёт любой новый) и Developer Pack / Targeting Pack для .NET Framework 4.8.

```powershell
dotnet build OfflineChatter.sln -c Release
dotnet test  OfflineChatter.sln
```

Результат появится в `src/OfflineChatter/bin/Release/net48/`.

## Структура папок рядом с exe

```
OfflineChatter.exe
engine/<tag>-<backend>/   распакованные сборки llama.cpp
config/                   settings.json, profiles/, providers.json, mcp.json, models.json, certs/
models/                   GGUF (путь настраивается)
chats/                    история чатов (JSON на каждый чат)
reports/                  отчёты бенчмарков
logs/                     app-*.jsonl, engine-*.log
cache/
```

Если в папку с exe нельзя писать (например, это `Program Files`), данные сохраняются в `%LOCALAPPDATA%\OfflineChatter`.
Файл `portable.flag` рядом с exe принудительно включает портативный режим.

## Структура исходников

```
src/OfflineChatter.Core/   логика без WPF: сеть, движок, модели, провайдеры, MCP, экспорт, хранилище
src/OfflineChatter/        WPF-приложение: темы, окна, контролы
tests/                     xUnit-тесты Core
```

Правила для разработчиков и ИИ-агентов описаны в [AGENTS.md](AGENTS.md).

## Лицензия

[Apache 2.0](LICENSE)
