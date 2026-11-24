<#
  Instala o Odradek nativo (C# + Avalonia) no seu usuário e cria os atalhos. Troca o app Python pelo nativo sem perder dados:
  o jobs.db é o mesmo (%LOCALAPPDATA%\Odradek) e o app novo migra o esquema sozinho na primeira abertura.

  .\build.ps1                         # gera dist\Odradek
  .\install.ps1                       # instala em %LOCALAPPDATA%\Programs\Odradek, com atalhos no Menu Iniciar e na Área de Trabalho
  .\install.ps1 -NoDesktopShortcut    # só o atalho do Menu Iniciar
  .\install.ps1 -Uninstall            # remove o app e os atalhos (os dados em %LOCALAPPDATA%\Odradek ficam)

  Antes de copiar, o script guarda uma cópia do banco em <pasta de dados>\backup-antes-do-nativo-<data>\.
  Feche o Odradek (Sair no menu da bandeja) antes de instalar: o script recusa seguir com ele aberto.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "dist\Odradek"),
    [string]$Target = (Join-Path $env:LOCALAPPDATA "Programs\Odradek"),
    [switch]$NoDesktopShortcut,
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"

$dataDir = if ($env:ODRADEK_DATA_DIR) { $env:ODRADEK_DATA_DIR } else { Join-Path $env:LOCALAPPDATA "Odradek" }
$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "Odradek.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "Odradek.lnk"

function Assert-Closed {
    $running = Get-Process -Name Odradek -ErrorAction SilentlyContinue
    if ($running) { throw "O Odradek está aberto. Feche-o pelo menu da bandeja (Sair) e rode o script de novo." }
}

function New-Shortcut([string]$Path, [string]$Exe) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Exe
    $link.WorkingDirectory = Split-Path $Exe
    $link.IconLocation = "$Exe,0"
    $link.Description = "Odradek: coleta, triagem e candidaturas"
    $link.Save()
}

Assert-Closed

if ($Uninstall) {
    foreach ($item in $startMenu, $desktop) { if (Test-Path $item) { Remove-Item $item -Force; Write-Host "Removido: $item" } }
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force; Write-Host "Removido: $Target" }
    Write-Host "Os dados continuam em $dataDir."
    return
}

if (-not (Test-Path (Join-Path $Source "Odradek.exe"))) { throw "Não achei $Source\Odradek.exe. Rode .\build.ps1 primeiro." }

# O app precisa do .NET 10 e do ASP.NET Core 10 (a pasta publicada não os leva junto).
$runtimes = (& dotnet --list-runtimes 2>$null) -join "`n"
foreach ($needed in "Microsoft.NETCore.App 10.", "Microsoft.AspNetCore.App 10.") {
    if ($runtimes -notmatch [regex]::Escape($needed)) {
        throw "Falta o runtime '$needed' do .NET. Instale o .NET 10 Desktop Runtime e o ASP.NET Core Runtime 10 (versão x64) em https://dotnet.microsoft.com/download/dotnet/10.0"
    }
}

# Cópia de segurança do banco antes de o app novo mexer no esquema.
$db = Join-Path $dataDir "jobs.db"
if (Test-Path $db) {
    $backup = Join-Path $dataDir ("backup-antes-do-nativo-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($suffix in "", "-wal", "-shm") { if (Test-Path "$db$suffix") { Copy-Item "$db$suffix" $backup } }
    Write-Host "Cópia do banco: $backup"
} else {
    Write-Host "Ainda não há jobs.db em $dataDir (instalação nova): o app cria o banco na primeira abertura."
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null
robocopy $Source $Target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "A cópia dos arquivos falhou (robocopy $LASTEXITCODE)." }

$exe = Join-Path $Target "Odradek.exe"
New-Shortcut $startMenu $exe
if (-not $NoDesktopShortcut) { New-Shortcut $desktop $exe }

Write-Host ""
Write-Host "Instalado em $Target"
Write-Host "Atalho: $startMenu"
Write-Host "Dados:  $dataDir (o mesmo jobs.db do app Python; o esquema é migrado na primeira abertura)"
Write-Host "Se algo der errado, restaure a cópia do banco acima e abra o app Python (dist\Odradek\Odradek.exe)."
