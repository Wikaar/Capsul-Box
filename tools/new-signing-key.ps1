# Capsul Box: создать постоянный ключ подписи APK (один раз).
# Запуск: правой кнопкой по файлу → «Выполнить с помощью PowerShell»
# или в PowerShell:  powershell -ExecutionPolicy Bypass -File .\new-signing-key.ps1
#
# Ключ и пароль сохраняются в папке %USERPROFILE%\CapsulBox-signing. Сделай копию этой папки:
# без ключа новые версии APK нельзя будет поставить поверх старых.

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:USERPROFILE 'CapsulBox-signing'
$jks = Join-Path $dir 'capsulbox.jks'
$alias = 'capsulbox'

# keytool: из Android Studio, JAVA_HOME или PATH
$candidates = @(
    "$env:ProgramFiles\Android\Android Studio\jbr\bin\keytool.exe",
    "$env:ProgramFiles\Android\Android Studio\jre\bin\keytool.exe",
    "$env:LOCALAPPDATA\Programs\Android Studio\jbr\bin\keytool.exe"
)
if ($env:JAVA_HOME) { $candidates += (Join-Path $env:JAVA_HOME 'bin\keytool.exe') }
$keytool = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $keytool) { $keytool = (Get-Command keytool.exe -ErrorAction SilentlyContinue).Source }
if (-not $keytool) {
    Write-Host 'Не нашёл keytool. Он есть в Android Studio или в любой Java (JDK 17+): установи одно из них и запусти скрипт снова.' -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
if (Test-Path $jks) {
    Write-Host "Ключ уже есть: $jks — новый не создаю, показываю значения для секретов." -ForegroundColor Yellow
    $password = Get-Content (Join-Path $dir 'password.txt') -Raw
} else {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'
    & $keytool -genkeypair -keystore $jks -storetype PKCS12 -alias $alias -keyalg RSA -keysize 3072 -validity 36500 `
        -storepass $password -keypass $password -dname 'CN=Capsul Box, O=Wikaar' | Out-Null
    Set-Content -Path (Join-Path $dir 'password.txt') -Value $password -NoNewline
}
$password = $password.Trim()
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($jks))
Set-Content -Path (Join-Path $dir 'CAPSULBOX_KEYSTORE_B64.txt') -Value $b64 -NoNewline

Write-Host ''
Write-Host 'Готово. Добавь 4 секрета: github.com/Wikaar/Capsul-Box → Settings → Secrets and variables → Actions → New repository secret' -ForegroundColor Green
Write-Host ''
Write-Host "  CAPSULBOX_KEYSTORE_B64   = содержимое файла $dir\CAPSULBOX_KEYSTORE_B64.txt (уже скопировано в буфер обмена)"
Write-Host "  CAPSULBOX_STORE_PASSWORD = $password"
Write-Host "  CAPSULBOX_KEY_PASSWORD   = $password"
Write-Host "  CAPSULBOX_KEY_ALIAS      = $alias"
Write-Host ''
Write-Host "Сохрани копию папки $dir в надёжном месте." -ForegroundColor Yellow
Set-Clipboard -Value $b64
