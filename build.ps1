<#
  Gera o Odradek nativo (C# + Avalonia): dist\Odradek\Odradek.exe e dist\Odradek-win-x64.zip.

  .\build.ps1               # testa e publica
  .\build.ps1 -SkipTests    # só publica

  A pasta publicada é self-contained (win-x64): leva o .NET junto e roda num Windows sem nada instalado.
  O instalador é gerado a partir dela por installer\Odradek.iss; quem publica as versões para os usuários é o
  workflow .github\workflows\release.yml (GitHub Releases).
  Dados do usuário: %LOCALAPPDATA%\Odradek (o mesmo jobs.db do app Python; ODRADEK_DATA_DIR muda a pasta).

  Atualização automática: além de dist\, o build publica a versão no FEED (pasta local ou de rede): Odradek-<versão>-win-x64.zip e latest.json.
  Os apps instalados por install.ps1 consultam essa pasta e se atualizam sozinhos.
  .\build.ps1 -Feed D:\feed        # outra pasta-feed (padrão: $env:ODRADEK_UPDATE_FEED, senão <repositório>\releases)
  .\build.ps1 -NoFeed              # não publica no feed
  .\build.ps1 -Version 1.2.3       # versão explícita (padrão: aaaa.MMdd.HHmm da hora do build, sempre crescente)
#>
param(
    [switch]$SkipTests,
    [string]$Version,
    [string]$Feed,
    [switch]$NoFeed
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Version) {
    $now = Get-Date
    $Version = "{0}.{1}.{2}" -f $now.Year, ($now.Month * 100 + $now.Day), ($now.Hour * 100 + $now.Minute)
}
$parsed = $null
if (-not [version]::TryParse($Version, [ref]$parsed)) { throw "Versão inválida: '$Version' (use o formato 1.2.3 ou 1.2.3.4)." }
if (-not $Feed) { $Feed = if ($env:ODRADEK_UPDATE_FEED) { $env:ODRADEK_UPDATE_FEED } else { Join-Path $PSScriptRoot "releases" } }
$Feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Feed)

if (-not $SkipTests) {
    dotnet test Odradek.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Os testes falharam: nada foi publicado." }
}

$out = Join-Path $PSScriptRoot "dist\Odradek"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\Odradek.App\Odradek.App.csproj --configuration Release --runtime win-x64 --self-contained true --output $out "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

# A extensão do pacote leva a versão do app: é por ela que a extensão já carregada no navegador percebe que mudou e se recarrega.
$manifestPath = Join-Path $out "extension\manifest.json"
$manifestText = [System.IO.File]::ReadAllText($manifestPath)
$versionPattern = [regex]'"version"\s*:\s*"[^"]*"'
if (-not $versionPattern.IsMatch($manifestText)) { throw "Não achei a versão em $manifestPath." }
[System.IO.File]::WriteAllText($manifestPath, $versionPattern.Replace($manifestText, "`"version`": `"$Version`"", 1), (New-Object System.Text.UTF8Encoding($false)))

# Os símbolos de depuração só pesam: ficam de fora do pacote.
Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item -Force
$zip = Join-Path $PSScriptRoot "dist\Odradek-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)

if (-not $NoFeed) {
    New-Item -ItemType Directory -Force -Path $Feed | Out-Null
    $feedZipName = "Odradek-$Version-win-x64.zip"
    Copy-Item $zip (Join-Path $Feed $feedZipName) -Force
    $manifest = [ordered]@{
        version      = $Version
        file         = $feedZipName
        sha256       = (Get-FileHash (Join-Path $Feed $feedZipName) -Algorithm SHA256).Hash.ToLowerInvariant()
        published_at = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    # O manifesto só passa a apontar para o zip depois de ele estar inteiro no feed, e é trocado de uma vez (sem leitura pela metade).
    $tmp = Join-Path $Feed "latest.json.tmp"
    [System.IO.File]::WriteAllText($tmp, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item $tmp (Join-Path $Feed "latest.json") -Force
    # Só os 3 zips mais recentes ficam (um app que baixa agora ainda acha o arquivo que o manifesto anterior citava).
    Get-ChildItem $Feed -Filter "Odradek-*-win-x64.zip" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 3 | Remove-Item -Force
}

Write-Host ""
Write-Host "Pronto: $out\Odradek.exe ($size MB), versão $Version"
Write-Host "Zip:    $zip"
if (-not $NoFeed) { Write-Host "Feed:   $Feed (latest.json -> $Version)" }
