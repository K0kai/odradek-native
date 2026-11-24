# Odradek (nativo)

App de desktop do Odradek em **C# / .NET 10 + Avalonia 12**, só Windows: coleta de vagas, triagem por IA, candidaturas, API local para a extensão Odradek Autofill e para o Qpid, bandeja do sistema e backup para Postgres (Supabase/Neon).

Veio do repositório `job-scraper` (app Python + React em pywebview). Usa o **mesmo `jobs.db`** (`%LOCALAPPDATA%\Odradek`, ou `ODRADEK_DATA_DIR`) e a **mesma porta 127.0.0.1:8765** (`PORT` muda), então a extensão e o Qpid continuam como estão. O app migra o esquema do banco sozinho ao abrir.

## Estrutura

```
src/Odradek.Core/      dados, motores (fila, triagem, coletor, IA), backup, API em memória; sem UI
src/Odradek.LocalApi/  Kestrel em 127.0.0.1: /health, extensão e Qpid
src/Odradek.App/       Avalonia: janela, telas, bandeja, aviso, instância única
tests/Odradek.Core.Tests/   xunit
tools/Odradek.Probe/   paridade com o app Python (precisa de bancos e referências geradas por ele)
```

## Comandos

```powershell
dotnet test Odradek.sln          # testes
.\build.ps1                      # testa e publica dist\Odradek\Odradek.exe (+ zip); precisa do .NET 10 Desktop e ASP.NET Core 10 (x64)
.\install.ps1                    # instala em %LOCALAPPDATA%\Programs\Odradek, cria atalhos e guarda uma cópia do banco (-Uninstall remove)
```

## Atualização automática

O app instalado por `install.ps1` se atualiza sozinho, sem perguntar nada:

1. `.\build.ps1` publica cada build no **feed** (pasta local ou de rede): `Odradek-<versão>-win-x64.zip` + `latest.json` (versão, arquivo, SHA-256). A versão padrão é `aaaa.MMdd.HHmm` da hora do build (`-Version` fixa uma; `-Feed` troca a pasta; `-NoFeed` não publica). O feed padrão é `$env:ODRADEK_UPDATE_FEED` ou `releases\` (ignorada pelo git).
2. `.\install.ps1` grava o feed em `update-source.txt` ao lado do `Odradek.exe`. **Só uma cópia com esse arquivo se atualiza**: rodar de `bin\` ou `dist\` nunca troca nada.
3. O app consulta o feed 2 min depois de abrir e a cada 30 min. Achando versão mais nova, baixa, confere o SHA-256 e extrai ao lado da instalação (`Odradek.staging`).
4. A troca acontece quando o app está **ocioso**: janela escondida na bandeja, nada em execução (fila de IA, coleta, backup) e a extensão/Qpid sem requisições há 5 min. O app sai pelo caminho normal (para os motores, backup ao sair), um script oculto troca as pastas e reabre o app **na bandeja**; o log do painel registra "Odradek atualizado de X para Y". Se nunca ficar ocioso, a versão preparada é aplicada ao **Sair** (sem reabrir) ou, no máximo, na próxima abertura.
5. Se o app novo não responder em `/health` em 60 s, o script volta para a versão anterior, reabre-a e marca a versão em `updates\failed.txt` (nunca mais tentada). Tudo fica em `update.log`, na pasta de dados.

`ODRADEK_UPDATE=0` desliga; `ODRADEK_UPDATE_FEED` troca o feed gravado na instalação; `ODRADEK_UPDATE_INTERVAL_SEC=N` usa N segundos em todos os tempos acima (para conferir o fluxo). Quem tem acesso de escrita ao feed controla o que os apps instalados executam: proteja a pasta como protegeria o próprio app.

## Variáveis úteis

| Variável | Efeito |
|---|---|
| `ODRADEK_DATA_DIR` | troca a pasta de dados; também desliga o backup guardado no cofre (só `DATABASE_URL_STANDBY` vale) |
| `PORT` | porta local (padrão 8765) |
| `QPID_API_TOKEN` | token do Qpid (senão: cofre do Windows `job-scraper` / `qpid_api_token`, ou `qpid_token.txt`) |
| `DATABASE_URL_STANDBY` | URL do Postgres de backup (senão: Configurações › Contas e chaves) |
| `ODRADEK_THEME` | `light` ou `dark` na abertura |
| `ODRADEK_SHOTS`, `ODRADEK_SMOKE` | capturas de tela por tela e roteiros de teste automático (`shots`, `idle`, `1`, `2`) |

## Notas

- O NuGet global desta máquina é restritivo (`nuget.config` lista os pacotes permitidos): pacote novo precisa entrar na lista.
- A cópia para Postgres só foi testada com doubles e com a conversão de tipos; nunca contra um Postgres real.
- As chaves de API ficam no cofre do Windows (serviço `job-scraper`) e não vão no backup.
