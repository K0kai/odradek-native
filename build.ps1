<#
  Gera o Odradek nativo (C# + Avalonia): dist\Odradek\Odradek.exe e dist\Odradek-win-x64.zip.

  .\build.ps1               # testa e publica
  .\build.ps1 -SkipTests    # só publica

  A pasta publicada precisa do .NET 10 Desktop Runtime e do ASP.NET Core 10 Runtime instalados no Windows (versão "x64").
  Não há instalador: o app é uma pasta. Copie-a para onde quiser e crie o atalho para Odradek.exe.
  Dados do usuário: %LOCALAPPDATA%\Odradek (o mesmo jobs.db do app Python; ODRADEK_DATA_DIR muda a pasta).
#>
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test Odradek.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Os testes falharam: nada foi publicado." }
}

$out = Join-Path $PSScriptRoot "dist\Odradek"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\Odradek.App\Odradek.App.csproj --configuration Release --runtime win-x64 --self-contained false --output $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

# Os símbolos de depuração só pesam: ficam de fora do pacote.
Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item -Force
$zip = Join-Path $PSScriptRoot "dist\Odradek-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "Pronto: $out\Odradek.exe ($size MB)"
Write-Host "Zip:    $zip"
