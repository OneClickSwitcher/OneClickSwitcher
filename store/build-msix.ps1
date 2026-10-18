# One Click Switcher: сборка пакета MSIX для Microsoft Store.
#
# Два режима:
#   .\build-msix.ps1          Пакет для отправки в Магазин (без подписи: подпишет сам Microsoft).
#   .\build-msix.ps1 -Test    Пробный пакет: временная подпись, установка на этот компьютер для проверки.
#                             Запускать от имени администратора.
param([switch]$Test)
$ErrorActionPreference = "Stop"

# ===== НАСТРОЙКИ =====
# Три значения ниже берутся в Партнёрском центре: Управление продуктом, Идентификация продукта.
# Значения уже вписаны (получены 11 октября 2026).
$StoreName = "DenysBoiko.OneClickSwitcher"                # Package/Identity/Name
$StorePublisher = "CN=6DF6E392-4678-4E0C-B87C-AB808E3C438F"  # Package/Identity/Publisher
$StorePublisherDisplayName = "Denys Boiko"                # PublisherDisplayName
$Version = "1.0.0.0"                                    # при каждом новом выпуске увеличивайте, последнюю цифру оставляйте 0

$proj = "C:\Tools\OneClickSwitcher"     # папка с проектом (Program.cs и .csproj)
$assets = "C:\Tools\StoreAssets"        # сюда распакован архив OCS-store-logos.zip
$work = "C:\Tools\StoreBuild"           # здесь собирается пакет
$pfxPassword = "test-only-password"

if ($Test)
{
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { Write-Host "СТОП: для пробного режима запустите PowerShell от имени администратора."; return }
    $Name = "OneClickSwitcher.Test"; $Publisher = "CN=OneClickSwitcherTest"; $PublisherDisplay = "Test"
}
else
{
    if ($StoreName -like "PUT-*" -or $StorePublisher -like "*PUT-*")
    {
        Write-Host "СТОП: впишите в начале скрипта три значения из Партнёрского центра (раздел НАСТРОЙКИ)."
        Write-Host "      Для пробной сборки на своём компьютере запустите: .\build-msix.ps1 -Test"
        return
    }
    $Name = $StoreName; $Publisher = $StorePublisher; $PublisherDisplay = $StorePublisherDisplayName
}
if (-not (Test-Path "$assets\Square44x44Logo.scale-100.png")) { Write-Host "СТОП: распакуйте OCS-store-logos.zip в $assets"; return }

# ===== 1. Инструменты Windows SDK =====
function Find-Tool($name, $folder)
{
    Get-ChildItem $folder -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\*" } |
        Sort-Object FullName -Descending | Select-Object -First 1
}
$makeappx = $null; $makepri = $null; $signtool = $null
foreach ($f in @("C:\Program Files (x86)\Windows Kits\10\bin", "C:\Tools\MsixTools"))
{
    if (-not $makeappx) { $makeappx = Find-Tool "makeappx.exe" $f }
    if (-not $makepri) { $makepri = Find-Tool "makepri.exe" $f }
    if (-not $signtool) { $signtool = Find-Tool "signtool.exe" $f }
}
if (-not $makeappx -or -not $makepri -or -not $signtool)
{
    Write-Host "Скачиваю официальный набор инструментов Microsoft (несколько сотен МБ), подождите..."
    $tools = "C:\Tools\MsixTools"
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $zip = "$tools\buildtools.zip"
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/10.0.26100.9169" -OutFile $zip
    Expand-Archive $zip -DestinationPath "$tools\pkg" -Force
    Remove-Item $zip
    $makeappx = Find-Tool "makeappx.exe" $tools; $makepri = Find-Tool "makepri.exe" $tools; $signtool = Find-Tool "signtool.exe" $tools
}
if (-not $makeappx -or -not $makepri -or -not $signtool) { Write-Host "СТОП: не найдены makeappx, makepri, signtool."; return }

# ===== 2. Сборка программы для Магазина (без диагностики, в ней есть запись звука) =====
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
$pkg = "$work\pkg"
New-Item -ItemType Directory -Path "$pkg\Assets" -Force | Out-Null
Write-Host "Собираю программу для Магазина..."
& dotnet publish $proj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DefineConstants=STORE -o "$work\publish" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА сборки: запустите команду dotnet publish вручную и пришлите текст ошибки."; return }
Copy-Item "$work\publish\OneClickSwitcher.exe" "$pkg\OneClickSwitcher.exe"

# ===== 3. Значки и паспорт пакета (манифест) =====
foreach ($pat in @("Square44x44Logo.*", "Square150x150Logo.*", "Wide310x150Logo.*", "SplashScreen.*", "StoreLogo.*"))
{
    Copy-Item "$assets\$pat" "$pkg\Assets" -Force
}
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap5 rescap">
  <Identity Name="__NAME__" Publisher="__PUBLISHER__" Version="__VERSION__" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>One Click Switcher</DisplayName>
    <PublisherDisplayName>__PUBLISHERDISPLAY__</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-US" />
    <Resource Language="ru-RU" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="OneClickSwitcher.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="One Click Switcher" Description="Switch Bluetooth, Wi-Fi, microphone and sound with one click on a shortcut." BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
        <uap:SplashScreen Image="Assets\SplashScreen.png" />
      </uap:VisualElements>
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias" Executable="OneClickSwitcher.exe" EntryPoint="Windows.FullTrustApplication">
          <uap5:AppExecutionAlias>
            <uap5:ExecutionAlias Alias="OneClickSwitcher.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <DeviceCapability Name="radios" />
  </Capabilities>
</Package>
'@
$manifest = $manifest.Replace("__NAME__", $Name).Replace("__PUBLISHER__", $Publisher).Replace("__VERSION__", $Version).Replace("__PUBLISHERDISPLAY__", $PublisherDisplay)
[System.IO.File]::WriteAllText("$pkg\AppxManifest.xml", $manifest, (New-Object System.Text.UTF8Encoding $false))

# ===== 4. Индекс ресурсов (по нему Windows выбирает значок нужного размера) =====
& $makepri.FullName createconfig /cf "$work\priconfig.xml" /dq en-US /pv 10.0.0 /o | Out-Null
& $makepri.FullName new /pr $pkg /cf "$work\priconfig.xml" /mn "$pkg\AppxManifest.xml" /of "$pkg\resources.pri" /o | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА makepri: запустите команду вручную и пришлите текст."; return }
Remove-Item "$pkg\resources.pri.xml" -ErrorAction SilentlyContinue

# ===== 5. Упаковка =====
$out = "$work\OneClickSwitcher_${Version}_x64.msix"
& $makeappx.FullName pack /d $pkg /p $out /o
if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА makeappx: пришлите текст выше."; return }

if (-not $Test)
{
    Write-Host ""
    Write-Host "ГОТОВО: пакет для Магазина: $out"
    Write-Host "Загрузите его в Партнёрском центре: Отправка, Пакеты."
    return
}

# ===== 6. Только для -Test: временная подпись, установка, тестовые ярлыки =====
Get-AppxPackage -Name $Name | Remove-AppxPackage -ErrorAction SilentlyContinue
Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Remove-Item
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq $Publisher } | Remove-Item
$cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "OneClickSwitcher test" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$sec = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "$work\test.pfx" -Password $sec | Out-Null
Export-Certificate -Cert $cert -FilePath "$work\test.cer" | Out-Null
Import-Certificate -FilePath "$work\test.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
& $signtool.FullName sign /fd SHA256 /f "$work\test.pfx" /p $pfxPassword $out
if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА signtool: пришлите текст выше."; return }
Add-AppxPackage $out
Write-Host ""
Write-Host "ГОТОВО: пробный пакет установлен. Найдите One Click Switcher в Пуске и запустите."
Write-Host "Удалить: Параметры, Приложения, One Click Switcher, Удалить."
