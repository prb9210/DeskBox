# DeskBox 自动更新（由计划任务调用：登录时 + 每 2 小时）
# 数据来源：坚果云同步下来的「DeskBox更新」文件夹（manifest.json + zip）
$ErrorActionPreference = 'SilentlyContinue'
$install = Join-Path $env:LOCALAPPDATA 'DeskBox'
$logPath = Join-Path $install 'update.log'

function Write-Log([string]$msg) {
    try {
        if (Test-Path $logPath) {
            if ((Get-Item $logPath).Length -gt 1048576) { Move-Item $logPath "$logPath.1" -Force }
        }
        Add-Content -Path $logPath -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $msg) -Encoding UTF8
    } catch { }
}

if (-not (Test-Path (Join-Path $install 'DeskBox.exe'))) { Write-Log 'DeskBox 未安装，跳过'; exit 0 }

# ---- 1. 找更新源 ----
$candidates = New-Object System.Collections.ArrayList
$overrideFile = Join-Path $install 'update-source.txt'
if (Test-Path $overrideFile) {
    $custom = (Get-Content $overrideFile -Raw).Trim()
    if ($custom) { [void]$candidates.Add($custom) }
}
$common = @(
    'C:\同步\DeskBox更新',
    "$env:USERPROFILE\我的坚果云\DeskBox更新",
    "$env:USERPROFILE\Nutstore\我的坚果云\DeskBox更新",
    "$env:USERPROFILE\坚果云\我的坚果云\DeskBox更新",
    "$env:USERPROFILE\Nutstore\DeskBox更新",
    "$env:USERPROFILE\Documents\坚果云\DeskBox更新"
)
foreach ($p in $common) { [void]$candidates.Add($p) }
foreach ($drive in (Get-PSDrive -PSProvider FileSystem)) {
    [void]$candidates.Add((Join-Path $drive.Root '我的坚果云\DeskBox更新'))
    [void]$candidates.Add((Join-Path $drive.Root '坚果云\DeskBox更新'))
}

$sourceDir = $null
foreach ($dir in $candidates) {
    if ($dir -and (Test-Path (Join-Path $dir 'manifest.json'))) { $sourceDir = $dir; break }
}
if (-not $sourceDir) { Write-Log '没有找到更新文件夹（manifest.json）'; exit 0 }

# ---- 2. 读清单、比版本 ----
$manifest = Get-Content (Join-Path $sourceDir 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $manifest.version -or -not $manifest.package) { Write-Log 'manifest.json 不完整'; exit 0 }

$versionFile = Join-Path $install 'version.txt'
$current = '0.0.0'
if (Test-Path $versionFile) { $current = (Get-Content $versionFile -Raw).Trim() }

try {
    if ([version]$manifest.version -le [version]$current) { exit 0 }
} catch {
    Write-Log ('版本号无法比较：' + $manifest.version); exit 0
}

$zipPath = Join-Path $sourceDir $manifest.package
if (-not (Test-Path $zipPath)) { Write-Log ('包还没同步下来：' + $manifest.package); exit 0 }

$size1 = (Get-Item $zipPath).Length
Start-Sleep -Seconds 3
$size2 = (Get-Item $zipPath).Length
if ($size1 -ne $size2) { Write-Log '包还在同步，下次再试'; exit 0 }

if ($manifest.sha256) {
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $manifest.sha256.ToLower()) { Write-Log 'sha256 不符（可能仍在同步），下次再试'; exit 0 }
}

# ---- 3. 解包校验 ----
$temp = Join-Path $env:TEMP ('DeskBoxUpd_' + [guid]::NewGuid().ToString('N'))
Expand-Archive -Path $zipPath -DestinationPath $temp -Force
$payload = Join-Path $temp 'payload'
if (-not (Test-Path (Join-Path $payload 'DeskBox.exe'))) {
    Write-Log '包结构不对，缺 payload\DeskBox.exe'
    Remove-Item $temp -Recurse -Force
    exit 0
}

# ---- 4. 关程序 → 覆盖 → 记版本 → 重启 ----
$wasRunning = $false
if (Get-Process -Name 'DeskBox' -ErrorAction SilentlyContinue) { $wasRunning = $true }
Get-Process -Name 'DeskBox' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'deskbox-smart' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$robo = Start-Process -FilePath 'robocopy.exe' -ArgumentList @($payload, $install, '/E', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -Wait -PassThru -WindowStyle Hidden
if ($robo.ExitCode -ge 8) {
    Write-Log ('复制失败，robocopy 退出码 ' + $robo.ExitCode)
    Remove-Item $temp -Recurse -Force
    exit 1
}

Set-Content -Path $versionFile -Value $manifest.version -Encoding ASCII
Write-Log ('已更新 ' + $current + ' → ' + $manifest.version + '（来源 ' + $sourceDir + '）')
Remove-Item $temp -Recurse -Force

if ($wasRunning) {
    Start-Sleep -Seconds 1
    Start-Process -FilePath (Join-Path $install 'DeskBox.exe')
    Write-Log '已重启 DeskBox'
}
exit 0
