# One Click Switcher

Маленькая утилита для Windows 11: переключатели и действия в один клик с панели задач или рабочего стола. Без значка в трее и без фоновых процессов: программа запускается по клику, делает своё дело и закрывается.

## Что умеет

- Bluetooth и Wi-Fi: включить или выключить
- Микрофон (все микрофоны сразу) и звук (устройство вывода по умолчанию)
- Блокировка, сон, выключение (с отсчётом 5 секунд и отменой)
- Статус: плашка с состоянием всех переключателей

## Как это работает

Один файл OneClickSwitcher.exe и по одному ярлыку на каждую функцию. Функция задаётся параметром в ярлыке:

    OneClickSwitcher.exe --toggle bluetooth
    OneClickSwitcher.exe --toggle wifi
    OneClickSwitcher.exe --toggle mic
    OneClickSwitcher.exe --toggle sound
    OneClickSwitcher.exe --action lock
    OneClickSwitcher.exe --action sleep
    OneClickSwitcher.exe --action shutdown
    OneClickSwitcher.exe --status

Ярлык можно закрепить на панели задач. В Windows 11 пункт находится так: правая кнопка по ярлыку, затем "Показать дополнительные параметры", затем "Закрепить на панели задач".

## Сборка

Нужен .NET 10 SDK. В папке проекта:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

Готовый файл появится в папке bin\Release\...\publish.

## Известные ограничения

- Если Bluetooth-гарнитура с микрофоном подключилась после нажатия, её микрофон останется включённым: отключаются микрофоны, активные в момент клика.
- Переключатель камеры в этой версии не реализован.
- На некоторых HDMI-мониторах звук щелчка при выключении может быть не слышен.

## Лицензии

Используется библиотека NAudio (лицензия MIT). Лицензия самого проекта пока не выбрана.
