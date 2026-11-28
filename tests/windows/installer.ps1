param([Parameter(Mandatory)][string]$Installer)

$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$local = [Environment]::GetFolderPath('LocalApplicationData')
$appDir = Join-Path $local 'Programs\LiuLianToolBox'
$exe = Join-Path $appDir 'LiuLianToolBox.exe'
$profileDir = Join-Path $local 'LiuLianToolBox'
$settings = Join-Path $profileDir 'settings.json'
$menuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'LiuLianToolBox.lnk'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'LiuLianToolBox.lnk'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LiuLianMagicBox.LiuLianToolBox_is1'
$uninstaller = Join-Path $appDir 'unins000.exe'

function Require($condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Wait-For([string]$message, [scriptblock]$condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (& $condition)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw $message }
        Start-Sleep -Milliseconds 150
    }
}
function Run-Setup([string]$binary, [string[]]$options = @()) {
    $flags = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-') + $options
    $setup = Start-Process -FilePath $binary -ArgumentList $flags -Wait -PassThru
    Require ($setup.ExitCode -eq 0) "Setup exited with code $($setup.ExitCode)."
}

# Only touch a fresh test account. Settings are deliberately retained by the uninstaller.
foreach ($path in @($appDir, $profileDir, $menuLink, $startupLink, $registration)) {
    Require (-not (Test-Path -LiteralPath $path)) "An existing installation or profile was found at $path."
}
Require (-not (Get-Process -Name LiuLianToolBox -ErrorAction SilentlyContinue)) 'ToolBox is already running.'

Add-Type -Namespace ToolBoxTest -Name Window -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
    uint flags, uint timeout, out IntPtr result);
'@

try {
    Run-Setup $Installer
    Require ((Test-Path $exe) -and (Test-Path $menuLink) -and (Test-Path $registration)) 'Missing app, Start menu shortcut or uninstall entry.'
    Require (-not (Test-Path $startupLink)) 'The sign-in shortcut must be opt-in.'

    [IO.Directory]::CreateDirectory($profileDir) | Out-Null
    $alarm = @{ alarm = @{ id = 'offline'; due = [DateTimeOffset]::UtcNow.ToString('o') }; fired = $false }
    @{ name = 'Installer test PC'; endpoint = 'https://www.liulian.dev/toolbox'; alarms = @{ offline = $alarm } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settings -Encoding utf8NoBOM
    $before = (Get-FileHash $settings).Hash
    Run-Setup $Installer @('/TASKS=startup')
    Require ((Get-FileHash $settings).Hash -eq $before) 'Reinstall changed the existing profile.'
    $shell = New-Object -ComObject WScript.Shell
    $startup = $shell.CreateShortcut($startupLink)
    Require (($startup.TargetPath -eq $exe) -and ($startup.Arguments -eq '--background')) 'Incorrect sign-in shortcut.'
    Require ($shell.CreateShortcut($menuLink).TargetPath -eq $exe) 'Incorrect Start menu target.'

    Start-Process -FilePath $startupLink
    Wait-For 'The saved alarm did not fire on a background start.' {
        (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).alarms.offline.fired
    }
    $primary = Get-Process -Name LiuLianToolBox
    Require ($primary.MainWindowHandle -eq [IntPtr]::Zero) 'Sign-in displayed the main window.'
    foreach ($opening in 1..2) {
        Start-Process -FilePath $menuLink
        Wait-For 'The Start menu did not restore the original window.' {
            $primary.Refresh()
            $primary.MainWindowHandle -ne [IntPtr]::Zero -and @(Get-Process -Name LiuLianToolBox).Count -eq 1
        }
        $window = $primary.MainWindowHandle
        Require ($primary.CloseMainWindow()) 'Could not close the window.'
        Wait-For 'Closing the window did not leave ToolBox running in the tray.' {
            $primary.Refresh()
            -not $primary.HasExited -and $primary.MainWindowHandle -eq [IntPtr]::Zero
        }
    }

    # End only the test window's session, exercising the shutdown path while it is hidden.
    $reply = [IntPtr]::Zero
    $one = [IntPtr]::new(1)
    $sent = [ToolBoxTest.Window]::SendMessageTimeout($window, 0x11, [IntPtr]::Zero, $one, 2, 3000, [ref]$reply)
    Require ($sent -ne [IntPtr]::Zero -and $reply -ne [IntPtr]::Zero) 'ToolBox vetoed session shutdown.'
    [void][ToolBoxTest.Window]::SendMessageTimeout($window, 0x16, $one, $one, 2, 3000, [ref]$reply)
    Require ($primary.WaitForExit(10000)) 'The hidden process did not exit at session shutdown.'

    $before = (Get-FileHash $settings).Hash
    Run-Setup $Installer @('/MERGETASKS=!startup')
    Require (-not (Test-Path $startupLink)) 'Deselecting sign-in left its shortcut installed.'
    Run-Setup $Installer @('/TASKS=startup')
    Run-Setup $uninstaller
    foreach ($path in @($exe, $menuLink, $startupLink, $registration)) {
        Require (-not (Test-Path -LiteralPath $path)) "Uninstall left $path behind."
    }
    Require ((Get-FileHash $settings).Hash -eq $before) 'Uninstall changed the profile.'
    Write-Host 'Verified install, sign-in, background alarm, close, reopen, shutdown, reinstall and uninstall.'
}
finally {
    Get-Process -Name LiuLianToolBox -ErrorAction SilentlyContinue |
        Where-Object Path -eq $exe | Stop-Process -Force
    if (Test-Path $uninstaller) { Run-Setup $uninstaller }
    if (Test-Path $settings) { Remove-Item -LiteralPath $settings }
    if (Test-Path $profileDir) { [IO.Directory]::Delete($profileDir) }
}
