param(
    [string]$GodotPath = "",
    [int]$Port = 24920,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($GodotPath)) {
    $godot = Get-Command godot, godot4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($godot) {
        $GodotPath = $godot.Source
    } else {
        $GodotPath = "C:\godot\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe"
    }
}
if (-not (Test-Path -LiteralPath $GodotPath)) {
    throw "Godot executable not found. Pass -GodotPath explicitly."
}
if (-not $SkipBuild) {
    & dotnet build (Join-Path $projectPath "Master_JS.csproj") --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}
$importPath = Join-Path $projectPath ".godot\imported"
if (-not (Test-Path -LiteralPath $importPath) -or (Get-ChildItem -LiteralPath $importPath -ErrorAction SilentlyContinue).Count -eq 0) {
    & $GodotPath --headless --path $projectPath --audio-driver Dummy --import
    if ($LASTEXITCODE -ne 0) { throw "Godot resource import failed" }
}

$logRoot = Join-Path ([IO.Path]::GetTempPath()) ("paakuri-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $logRoot | Out-Null
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-Paakuri([string]$name, [string[]]$userArguments) {
    $stdout = Join-Path $logRoot "$name.log"
    $arguments = @("--headless", "--path", $projectPath, "--audio-driver", "Dummy", "--log-file", $stdout, "--") + $userArguments
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $GodotPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start $name" }
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $processes.Add($process)
    return @{ Process = $process; Out = $stdout }
}

function Wait-Marker($run, [string]$marker, [int]$seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $run.Out) {
            $content = Get-Content -LiteralPath $run.Out -Raw
            if ($null -ne $content -and $content.Contains($marker)) { return }
        }
        if ($run.Process.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for '$marker'. Logs: $logRoot"
}

function Assert-NoErrors {
    foreach ($file in Get-ChildItem -LiteralPath $logRoot -File) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        if ($content -match "SCRIPT ERROR|Unhandled exception|ERROR:") {
            throw "Godot error in $($file.Name). Logs: $logRoot"
        }
    }
}

try {
    $protocol = Start-Paakuri "protocol" @("--paakuri-protocol-self-check")
    if (-not $protocol.Process.WaitForExit(30000)) { throw "Protocol self-check did not exit" }
    Wait-Marker $protocol "protocol self-check: PASS" 1

    $ai = Start-Paakuri "ai" @("--paakuri-ai")
    Wait-Marker $ai "GameManager ready: OfflineAi"
    $ai.Process.Kill()

    $server = Start-Paakuri "dedicated-server" @("--paakuri-dedicated=$Port")
    Wait-Marker $server "dedicated server listening"
    $clientOne = Start-Paakuri "dedicated-client-1" @("--paakuri-join=127.0.0.1:$Port", "--paakuri-auto-ready")
    $clientTwo = Start-Paakuri "dedicated-client-2" @("--paakuri-join=127.0.0.1:$Port", "--paakuri-auto-ready")
    Wait-Marker $server "GameManager ready: DedicatedServer"
    Wait-Marker $clientOne "GameManager ready: DedicatedClient"
    Wait-Marker $clientTwo "GameManager ready: DedicatedClient"
    $server.Process.Kill()
    $clientOne.Process.Kill()
    $clientTwo.Process.Kill()

    $p2pPort = $Port + 1
    $p2pHost = Start-Paakuri "p2p-host" @("--paakuri-host=$p2pPort", "--paakuri-auto-ready")
    Wait-Marker $p2pHost "host listening"
    $client = Start-Paakuri "p2p-client" @("--paakuri-join=127.0.0.1:$p2pPort", "--paakuri-auto-ready")
    Wait-Marker $p2pHost "GameManager ready: Host"
    Wait-Marker $client "GameManager ready: Client"
    Assert-NoErrors
    Write-Output "PAAKURI network smoke test: PASS"
    Write-Output "Logs: $logRoot"
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}
