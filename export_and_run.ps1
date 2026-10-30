# Exports the project with AOT and runs both repros headless, without and with the workaround.
# Usage: .\export_and_run.ps1 -GodotExe "C:\Path\To\Godot_v4.8-beta2_mono_win64.exe" [-SkipExport]
param(
    [Parameter(Mandatory = $true)][string]$GodotExe,
    [switch]$SkipExport
)

Set-Location $PSScriptRoot

if (-not $SkipExport) {
    if (Test-Path Export) { Remove-Item -Recurse -Force Export }
    New-Item -ItemType Directory -Force Export | Out-Null

    # vcvarsall.bat (used by AOT to find the linker) calls vswhere.exe by name, so it must be on the path
    $env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

    & $GodotExe --headless --path . --import | Out-Null
    $p = Start-Process -FilePath $GodotExe -ArgumentList '--headless', '--export-release', '"Windows Desktop"', 'Export/GodotGcRepro.exe', '--path', '.' -NoNewWindow -PassThru
    $p.WaitForExit()
}

function Run-Repro($name) {
    $arguments = @('--headless', '--', ('--repro=' + $name))
    $p = Start-Process -FilePath .\Export\GodotGcRepro.exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput "Export\$name.out.txt" -RedirectStandardError "Export\$name.err.txt"
    $null = $p.Handle # Needed for ExitCode to be available after the process exits
    if (-not $p.WaitForExit(120000)) { $p.Kill(); Write-Host "== $name : TIMEOUT" ; return }
    Write-Host "== $name : exit code $($p.ExitCode)"
    Get-Content "Export\$name.out.txt", "Export\$name.err.txt" | Where-Object { $_ -ne "" } | Select-Object -Last 8 | ForEach-Object { Write-Host "   $_" }
}

Run-Repro instantiate
Run-Repro instantiate-keepalive
Run-Repro reuse
Run-Repro reuse-keepalive
