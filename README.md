# HDVALHEIM

Клиентский BepInEx-мод для Valheim, который подменяет Texture2D по внутреннему имени без замены файлов `Valheim_Data/sharedassets*.assets`.

## Статус

Сейчас в репозитории находится исходная BepInEx-обвязка. Большой пакет HD-текстур будет добавлен отдельно, потому что GitHub-репозиторий не подходит для хранения многогигабайтных бинарных архивов как обычных файлов.

## Структура

```text
HDValheimUpdated/
├── Plugin.cs
├── HDValheimUpdated.csproj
├── install.ps1
└── HDValheimUpdated.cfg.example
```

## Как работает

1. BepInEx запускает `HDValheimUpdated.dll`.
2. Плагин ищет PNG/JPG в папке `Textures`.
3. Имя файла должно совпадать с внутренним именем Unity `Texture2D`.
4. Плагин находит уже загруженный `Texture2D` и загружает HD-картинку в тот же объект.
5. Файлы `Valheim_Data` не изменяются.

## Сборка

Нужны установленный Valheim, BepInEx и .NET SDK.

PowerShell:

```powershell
$env:VALHEIM_DIR="C:\Program Files (x86)\Steam\steamapps\common\Valheim"
dotnet build .\HDValheimUpdated\HDValheimUpdated.csproj -c Release
```

DLL появится в:

```text
HDValheimUpdated\bin\Release\netstandard2.1\HDValheimUpdated.dll
```

## Установка

Скопировать DLL в:

```text
Valheim\BepInEx\plugins\HDValheimUpdated\
```

HD-текстуры положить в:

```text
Valheim\BepInEx\plugins\HDValheimUpdated\Textures\
```

F8 — повторная загрузка текстур.

## Другу на сервере

Мод клиентский: он меняет только локальные текстуры. Другому игроку устанавливать его не требуется.
