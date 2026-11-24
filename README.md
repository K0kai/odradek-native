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
