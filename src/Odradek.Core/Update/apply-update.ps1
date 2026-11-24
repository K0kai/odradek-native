<#
  Troca a pasta do Odradek pela versão preparada pelo próprio app (Updater.cs). Roda oculto, fora da pasta que vai ser trocada.

  1. espera o processo antigo sair (até 5 min; se não sair, não mexe em nada);
  2. <instalação> -> <instalação>.old e <instalação>.staging -> <instalação> (se a segunda troca falhar, desfaz a primeira);
  3. com -Restart: abre o app novo e espera /health responder (60 s); se não responder, volta para a versão antiga e a reabre;
  4. uma versão que falhou entra em updates\failed.txt e nunca mais é tentada.

  Tudo o que acontece vai para <dados>\update.log.
#>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$InstallDir,
    [Parameter(Mandatory)][string]$StagingDir,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Version,
    [int]$Port = 8765,
    [switch]$Restart,
    [switch]$Background
)
$ErrorActionPreference = 'Stop'
$updates = Join-Path $DataDir 'updates'
Set-Location -LiteralPath $updates # nunca fica "dentro" da pasta que vai ser renomeada
$logFile = Join-Path $DataDir 'update.log'
$old = "$InstallDir.old"

function Write-Log([string]$Message) {
    try { Add-Content -LiteralPath $logFile -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message) -Encoding UTF8 } catch { }
}

# Antivírus e indexador seguram a pasta por alguns instantes: tenta de novo por ~15 s.
function Move-DirectoryWithRetry([string]$From, [string]$To) {
    for ($i = 0; $i -lt 30; $i++) {
        try { [System.IO.Directory]::Move($From, $To); return }
        catch { if ($i -eq 29) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

function Start-Odradek {
    $params = @{ FilePath = (Join-Path $InstallDir 'Odradek.exe'); WorkingDirectory = $InstallDir; PassThru = $true }
    if ($Background) { $params.ArgumentList = @('--background') }
    return Start-Process @params
}

function Test-OdradekHealthy($Process) {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { return $false }
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health" -UseBasicParsing -TimeoutSec 2
            if ($response.Content -match 'odradek-scraper') { return $true }
        } catch { }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

$mutex = New-Object System.Threading.Mutex($false, 'Local\Odradek.Update')
if (-not $mutex.WaitOne(0)) { Write-Log 'Outra troca já está em andamento; saindo.'; exit 0 }
try {
    Write-Log "Troca para a versão ${Version}: esperando o processo $ProcessId sair."
    $previous = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($previous -and -not $previous.WaitForExit(300000)) {
        Write-Log 'O app não saiu em 5 minutos; nada foi alterado.'
        exit 1
    }

    $swapped = $false
    $started = $null
    $ok = $false
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $StagingDir 'Odradek.exe'))) { throw 'a pasta preparada não existe mais' }
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
        Move-DirectoryWithRetry $InstallDir $old
        try { Move-DirectoryWithRetry $StagingDir $InstallDir }
        catch { Move-DirectoryWithRetry $old $InstallDir; throw }
        $swapped = $true
        Write-Log 'Pastas trocadas.'
        if ($Restart) {
            $started = Start-Odradek
            if (-not (Test-OdradekHealthy $started)) { throw 'o app novo fechou ou não respondeu em 60 s' }
            Write-Log 'App novo no ar.'
        }
        $ok = $true
    } catch {
        Write-Log ('Falha na troca: ' + $_.Exception.Message)
    }

    if ($ok) {
        Remove-Item -LiteralPath (Join-Path $updates 'staged.json') -Force -ErrorAction SilentlyContinue
        try { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction Stop } catch { Write-Log 'A pasta antiga ficou; o app a apaga na próxima abertura.' }
        Write-Log "Versão $Version instalada."
    } else {
        # A versão que falhou nunca mais é tentada (senão cada abertura repetiria a falha).
        try { Add-Content -LiteralPath (Join-Path $updates 'failed.txt') -Value $Version } catch { }
        Remove-Item -LiteralPath (Join-Path $updates 'staged.json') -Force -ErrorAction SilentlyContinue
        if ($swapped) {
            try {
                if ($started -and -not $started.HasExited) { $started.Kill(); $null = $started.WaitForExit(15000) }
                $bad = "$InstallDir.bad"
                if (Test-Path -LiteralPath $bad) { Remove-Item -LiteralPath $bad -Recurse -Force }
                Move-DirectoryWithRetry $InstallDir $bad
                Move-DirectoryWithRetry $old $InstallDir
                Remove-Item -LiteralPath $bad -Recurse -Force -ErrorAction SilentlyContinue
                Write-Log 'Versão anterior restaurada.'
            } catch { Write-Log ('Não consegui restaurar a versão anterior: ' + $_.Exception.Message) }
        }
        if ($Restart) {
            try { $null = Start-Odradek; Write-Log 'Versão anterior reaberta.' }
            catch { Write-Log ('Não consegui reabrir o app: ' + $_.Exception.Message) }
        }
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
