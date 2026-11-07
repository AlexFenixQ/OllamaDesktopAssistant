# 0.1.32 — 2026-11-07

- Исправлен отказ AVI со звуком PCM μ-law; расширен нативный набор видео/аудиокодеков и контейнеров, добавлен программный AV1/libaom.
- Расширены форматы изображений, включая HEIC/AVIF/JXL/RAW/PSD/JPEG XR; безопасная растеризация SVG без внешних загрузок, обработка GIF/APNG и нескольких страниц.
- Принудительное чтение ICO/TGA по проверенному формату, корректный выбор последнего кадра анимации, отсутствие увеличения маленьких кадров.
- Ранняя загрузка защитной политики изображений, проверки внешних SVG-ресурсов; исходники и лицензии новых нативных зависимостей включены.
- 154 теста форматов/регрессий и повторная интеграционная проверка ASR/продолжения/WIC; совместимость с указанными AVI проверена локально на фрагментах.
- Обновление 0.1.31 → 0.1.32 с сохранением истории, проектов, ключа, черновиков и файлов; резервные копии и откат.

# 0.1.31 — 2026-11-07

- Единые векторные значки, чёткое лого, тематические выпадающие списки, контекстные меню и полосы прокрутки; аккуратное поле запроса, условные кнопки отправки/остановки и прокрутки вниз.
- Документы до 512 МиБ; отдельные аудио/видео до 2 ГиБ и 6 часов. Сохраняются ограничения безопасного разбора содержимого.
- Встроенные FFmpeg/Whisper: автоматическая проверенная загрузка Small/Medium, полный локальный звук по 5-минутным блокам, таймкоды, TXT, контрольные точки и продолжение.
- Видео: весь звук плюс явно выборочные кадры по временной шкале/сменам сцен. Полный многоэтапный облачный разбор всех блоков текста, отчёт DOCX и TXT.
- Добавлены регрессии полного покрытия записи, неизвестных сроков/источников, форматов, совместимости и реального процесса импорта с отменой/продолжением.
- Подписанное обновление 0.1.30 → 0.1.31 с локальной резервной копией и откатом; история, ключ и файлы сохраняются.

# 0.1.30 — 2026-11-07

- Новый интерфейс: проекты и чаты в сворачиваемой панели, раскрываемые файлы, текстовые действия, Enter/Shift+Enter.
- Переименование и удаление проектов с сохранением чатов; перенос чата через список проектов.
- Подписанные обновления GitHub Releases, стабильный/бета-канал, тихая установка, проверенные копии и восстановление после сбоя.
- Сохранение черновика и вложений перед перезапуском, исправление контраста диалогов.

# 0.1.29 — 2026-11-07

Исправлены подтверждённые дефекты ZIP, импорта таблиц, валидации документов, проектов, сохранения параметров и потоковых инструментов. Закреплён SDK 10.0.403 и транзитивные зависимости. Добавлены регрессионные проверки и готовая Windows-сборка. Полный список: README_RU.md и AUDIT_RU.md. Схема данных сохранена.

# CHANGELOG — Ollama Desktop

## 0.1.28 (2026-10-26)

- Windows 10/11 compatibility: Inno Setup MinVersion lowered to Windows 10 1507 (10.0.10240), and app startup and owner bootstrap gates aligned.
- x64 build enables installation on Windows 11 ARM64 via x64 emulation (not validated). No 32-bit Windows build.
- New `scripts/Check-WindowsTarget.ps1` reports OS/architecture suitability without changing settings.
- Four preflight regression checks guard against versions diverging between installer and runtime.
- .NET 10 only officially supports specified Windows 10 LTSC/Enterprise editions; older systems remain experimental.
- No changes to chat, image, file parsing, document generation, user data schema or API credentials.

## 0.1.27 (2026-10-25)

- Removed fatal SUMMARY_SIZE check: verbose intermediary summaries no longer abort processing; partial-context use is explicitly reported when necessary.
- Added deterministic tabular profiles for large XLSX/CSV: all imported rows are aggregated into totals, SKU/warehouse leaders and coverage records by application code, not model estimates.
- CSV record parser supports quoted newlines, large exports (up to the safe file and row limits) and does not send all rows in one prompt.
- XLSX profile preserves formulas/cached values distinctions and does not execute macros.
- Added five Windows regression tests for table totals, stock calculations, streaming CSV and XLSX profiling.
- Safety limits remain: 25MiB/file, 160MiB decompressed, up to 1m tabular rows and 2m XLSX cells. No claim of unlimited cloud context.
- Known limits: arbitrary row-level search over large sources and deterministic creation of multi-sheet reports are not implemented; exact aggregate coverage depends on recognized headers and cached or literal values.

# CHANGELOG

## 0.1.26 — 2026-10-25
- Реализовано пересоздание последнего ответа: предыдущее сообщение и созданные файлы не удаляются, вложения запроса используются повторно при наличии исходных файлов.
- Основные кнопки в главном окне переведены на значки с всплывающими пояснениями и AutomationProperties.Name; копирование сохранено.
- Добавлены проекты и привязка к ним новых или существующих чатов. Инструкции и пользовательские заметки памяти вводятся вручную, локально хранятся в отдельной SQLite-базе.
- Изменён последовательный запрос недостающих форматов Word/Excel/PowerPoint, чтобы текстовое обещание модели не считалось созданием файла. Облачные результаты не гарантированы.
- Добавлены 5 автоматических тестов на выбор исходного сообщения, проекты, сохранение схемы основной базы и безопасность.
- Отдельно подготовлены синтетические XLSX/CSV для тестирования торговли, склада и больших выгрузок в стиле табличных экспортов 1С.
- Целевой стек и модель доступа Ollama Cloud не менялись.
