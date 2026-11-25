# Odradek (nativo)

App de desktop do Odradek em **C# / .NET 10 + Avalonia 12**, só Windows: coleta de vagas, triagem por IA, candidaturas, API local para a extensão Odradek Autofill e para o Qpid, bandeja do sistema e backup para Postgres (Supabase/Neon).

Veio do repositório `job-scraper` (app Python + React em pywebview). Usa o **mesmo `jobs.db`** (`%LOCALAPPDATA%\Odradek`, ou `ODRADEK_DATA_DIR`) e a **mesma porta 127.0.0.1:8765** (`PORT` muda), então a extensão e o Qpid continuam como estão. O app migra o esquema do banco sozinho ao abrir.

## Instalar

Baixe o **[Odradek-Setup.exe](https://github.com/K0kai/odradek-native/releases/latest/download/Odradek-Setup.exe)** e execute. Precisa de Windows 10 (1809) ou 11 de 64 bits; o .NET vai junto, e não pede administrador. O app vai para `%LOCALAPPDATA%\Programs\Odradek` e daí em diante se atualiza sozinho. Desinstalar (em "Adicionar ou remover programas") mantém os seus dados.

O instalador ainda não tem assinatura digital, então o Windows pode mostrar **"O Windows protegeu o computador"**: clique em **Mais informações → Executar assim mesmo**.

## Extensão do navegador

A extensão **Odradek Autofill** (pasta `extension/`) vem no pacote do app. No app: **Configurações → Contas e chaves → Extensão do navegador**, escolha o navegador e clique em **Instalar extensão** (o instalador também oferece abrir esse passo ao terminar). Navegadores não deixam um programa instalar uma extensão de fora da loja, nem abrir a página de extensões; por isso são alguns cliques: colar na barra o endereço que o app copia, ligar o **modo do desenvolvedor**, **Carregar sem compactação** e colar o caminho da pasta. Depois, no painel da extensão, **Connect to Odradek** e **Permitir** no app.

- Precisa de um navegador baseado no Chromium 141 ou mais novo. São oferecidos Chrome, Edge, Brave e **Opera GX** (136 ou mais novo, a primeira versão com painel lateral para extensões; conferido no 140); Opera e Vivaldi aparecem com o aviso de que não foram testados. Versões antigas do Opera pedem atualização. Firefox não funciona.
- Um navegador que não aparece na lista (instalado fora do lugar comum) pode ser escolhido em **Procurar outro…**.
- O navegador carrega a cópia em `%LOCALAPPDATA%\Odradek\extension`. O app a atualiza ao abrir (inclusive depois de se atualizar sozinho) e a extensão se recarrega quando o painel é aberto. Desinstalar o app não remove a extensão do navegador.
- O ID da extensão é fixo (`key` no `manifest.json`). O app só entrega token a pedidos com o `Origin` desse ID, e só depois do **Permitir**. Há um token por vez: conectar outro navegador desconecta o anterior.

## Publicar uma versão

1. Faça push do que vai na versão para a `main`.
2. No GitHub: **Actions → release → Run workflow** (ou `gh workflow run release`).

O workflow roda os testes, gera o pacote e o instalador e cria a release `v<aaaa.MMdd.HHmm>` com `latest.json`, o zip e o `Odradek-Setup.exe`. Os apps instalados a recebem sozinhos. **Não marque como "latest" uma release sem o `latest.json`**: é a release mais recente que os apps consultam. Para voltar atrás numa versão ruim, publique uma nova com a correção (o app nunca instala uma versão menor que a em uso).

Quem pode publicar releases no repositório controla o que os apps instalados executam: ative a verificação em duas etapas na conta do GitHub.

## Estrutura

```
src/Odradek.Core/      dados, motores (fila, triagem, coletor, IA), backup, API em memória; sem UI
src/Odradek.LocalApi/  Kestrel em 127.0.0.1: /health, extensão e Qpid
src/Odradek.App/       Avalonia: janela, telas, bandeja, aviso, instância única
tests/Odradek.Core.Tests/   xunit
tools/Odradek.Probe/   paridade com o app Python (precisa de bancos e referências geradas por ele)
installer/             o instalador (Inno Setup 6)
extension/             a extensão do navegador (Chromium, MV3); ver extension/README.md
```

## Comandos

```powershell
dotnet test Odradek.sln          # testes
.\build.ps1                      # testa e publica dist\Odradek\Odradek.exe (+ zip), self-contained win-x64
.\install.ps1                    # desenvolvimento: instala dist\ com feed local, cria atalhos e guarda uma cópia do banco (-Uninstall remove)
iscc /DAppVersion=1.2.3 installer\Odradek.iss   # gera out\Odradek-Setup.exe a partir de dist\Odradek
```

## Atualização automática

O app instalado (pelo `Odradek-Setup.exe` ou por `install.ps1`) se atualiza sozinho, sem perguntar nada. O **feed** é um endereço `https://` (no instalador: as Releases do GitHub) ou uma pasta local/de rede (no `install.ps1`); `http://` é recusado.

1. `.\build.ps1` publica cada build num feed em pasta: `Odradek-<versão>-win-x64.zip` + `latest.json` (versão, arquivo, SHA-256). A versão padrão é `aaaa.MMdd.HHmm` da hora do build (`-Version` fixa uma; `-Feed` troca a pasta; `-NoFeed` não publica). O feed padrão é `$env:ODRADEK_UPDATE_FEED` ou `releases\` (ignorada pelo git).
2. O instalador (ou o `.\install.ps1`) grava o feed em `update-source.txt` ao lado do `Odradek.exe`. **Só uma cópia com esse arquivo se atualiza**: rodar de `bin\` ou `dist\` nunca troca nada.
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
