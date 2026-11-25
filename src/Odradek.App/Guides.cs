using Odradek.Core.Collector;

namespace Odradek.App;

/// <summary>Um passo de um guia. Os opcionais viram botões: abrir um site, copiar um texto ou ir para outro guia.</summary>
internal sealed record GuideStep(string Title, string Text)
{
    public (string Label, string Url)? Link { get; init; }
    public (string Label, string Value)? Copy { get; init; }
    public string Next { get; init; } = "";
}

/// <summary>Um guia de configuração: passos numerados, avisos (custo e privacidade) e o que fazer quando der errado.</summary>
internal sealed record Guide(string Id, string Title, string Intro, GuideStep[] Steps)
{
    public string[] Warnings { get; init; } = [];
    public (string Problem, string Fix)[] Troubleshooting { get; init; } = [];
}

/// <summary>
/// O texto dos guias. Os nomes de campos e botões são os da tela, e o comportamento descrito é o do código: ao mudar um dos dois,
/// revise o guia correspondente. Os caminhos nos sites de terceiros foram conferidos em novembro de 2026.
/// </summary>
internal static class Guides
{
    public const string Start = "inicio", Gemini = "gemini", OpenAi = "openai", TokenHarbor = "tokenharbor", Apify = "apify", Filters = "filtros", Actors = "actors";

    public static readonly Guide[] All =
    [
        new(Start, "Comece por aqui",
            "Para a triagem por IA e a coleta no LinkedIn, cada passo depende do anterior. Siga nesta ordem.",
            [
                new("Crie a chave de IA", "Sem ela o app não analisa currículos, não gera filtros e não faz a triagem. Escolha um provedor: Gemini ou OpenAI.") { Next = Gemini },
                new("Envie o seu currículo", "Em Currículos, clique em “Enviar currículo” e espere a análise terminar (acompanhe em Filas). Preencha também a cidade e o país em Configurações › Perfil."),
                new("Gere o filtro principal", "Em Configurações › Busca, use “Gerar com IA” no Filtro principal.") { Next = Filters },
                new("Configure o Apify (para o LinkedIn)", "É o serviço que faz as buscas no LinkedIn e no Indeed.") { Next = Apify },
                new("Inicie a coleta", "Clique em “Iniciar coleta”, no menu lateral. O andamento aparece em Logs."),
            ])
        {
            Warnings = ["Quer só experimentar a coleta? Remotive e RemoteOK já vêm marcados em “Onde buscar” e não pedem chave."],
        },

        new(Gemini, "Chave do Gemini",
            "A chave é criada no Google AI Studio, com a sua conta Google.",
            [
                new("Abra a página de chaves", "Entre com a sua conta Google e aceite os termos, se pedirem. Para contas novas, o AI Studio pode criar sozinho um projeto e uma primeira chave.")
                    { Link = ("Abrir o Google AI Studio", "https://aistudio.google.com/apikey") },
                new("Crie e copie a chave", "Clique em “Create API key” e copie a chave. Chaves novas costumam começar com “AQ.”. Não a mande para ninguém."),
                new("Cole no app", "Em Configurações › Contas e chaves › Inteligência artificial, escolha o provedor Gemini, cole em “Chave da IA” e clique em “Salvar”. O selo ao lado do campo passa a mostrar “Configurada”."),
                new("Modelo", "Deixe o que já vem preenchido. Se ele estiver sobrecarregado, no limite ou fora do ar, o app tenta um modelo Gemini reserva por conta própria."),
            ])
        {
            Warnings =
            [
                "No nível gratuito há limite de pedidos por minuto e por dia, e o Google pode usar o que é enviado para melhorar os produtos dele. O app envia trechos do seu currículo e das vagas. Confira os termos e os limites atuais no site.",
                "Já tinha uma chave antiga? Na página de chaves, veja a coluna “Key Type”: o Google recusa chaves “Standard” sem restrição. Nesse caso, crie uma nova.",
            ],
            Troubleshooting =
            [
                ("Erro com HTTP 400 ou 403 e “API key not valid” ou “permission”", "A chave está errada, bloqueada ou sem permissão. Crie outra e cole de novo. Com a chave recusada, o app não passa para os modelos reserva."),
                ("“IA indisponível: nenhum modelo respondeu” com HTTP 429", "O limite foi atingido. Espere ele renovar ou configure o TokenHarbor como último recurso."),
                ("“Configure a chave da API de IA…”", "Não há chave salva para o provedor escolhido. Confira o campo “Provedor” e salve a chave."),
            ],
        },

        new(OpenAi, "Chave da OpenAI",
            "Alternativa ao Gemini. A API é paga à parte: uma assinatura do ChatGPT não dá crédito para ela.",
            [
                new("Coloque crédito", "Na plataforma da OpenAI, em Billing, compre crédito pré-pago (o mínimo é US$ 5). A recarga automática pode vir ligada: confira antes de confirmar.")
                    { Link = ("Abrir a plataforma da OpenAI", "https://platform.openai.com/") },
                new("Crie a chave", "Na página de chaves, clique em “Create new secret key” e copie na hora: ela não é mostrada de novo.")
                    { Link = ("Abrir a página de chaves", "https://platform.openai.com/api-keys") },
                new("Escolha o provedor antes de colar", "Em Configurações › Contas e chaves › Inteligência artificial, mude “Provedor” para OpenAI e só então cole em “Chave da IA”. Cada provedor guarda a sua própria chave."),
                new("Troque o modelo", "O campo “Modelo” vem com um modelo do Gemini. Substitua pelo ID de um modelo de texto atual da OpenAI, copiado da página de modelos, e clique em “Salvar”.")
                    { Link = ("Ver os modelos da OpenAI", "https://developers.openai.com/api/docs/models") },
            ])
        {
            Warnings =
            [
                "Cada análise, filtro, triagem e resposta do assistente consome crédito. O crédito comprado vence e não é reembolsável.",
                "Com a OpenAI não há um segundo modelo reserva: quando ela falha por limite ou instabilidade, a única alternativa é o TokenHarbor.",
            ],
            Troubleshooting =
            [
                ("HTTP 401", "A chave está errada ou foi revogada. Crie outra e cole de novo."),
                ("HTTP 429 ou “insufficient_quota”", "Falta crédito ou o limite foi atingido. Confira Billing e Limits na plataforma."),
                ("HTTP 404 ou “model_not_found”", "O ID em “Modelo” não existe ou a sua conta não tem acesso a ele."),
            ],
        },

        new(TokenHarbor, "TokenHarbor (opcional)",
            "É um último recurso: entra quando o Gemini ou a OpenAI falham por limite, instabilidade ou modelo indisponível. Ele não substitui a chave de IA: sem a chave principal, ou com ela recusada, o TokenHarbor não é usado.",
            [
                new("Crie a conta", "O cadastro gratuito não pede cartão.") { Link = ("Abrir o TokenHarbor", "https://tokenharbor.ai/") },
                new("Copie a chave", "No painel (dashboard), copie a chave, que começa com “thk_live_”. Ela é mostrada uma vez só; se perder, crie outra.")
                    { Link = ("Abrir o painel do TokenHarbor", "https://tokenharbor.ai/dashboard") },
                new("Ative os modelos gratuitos", "Os modelos terminados em “:free” só funcionam depois que você mesmo os ativa no painel do TokenHarbor, aceitando as condições deles."),
                new("Cole no app", "Em Configurações › Contas e chaves › TokenHarbor, cole em “Chave do TokenHarbor” e clique em “Salvar”."),
                new("Escolha os modelos", "Em “Modelos (em ordem)”, informe até 6 IDs separados por vírgula. O app tenta um de cada vez. A lista muda com o tempo: confira os IDs no site.")
                {
                    Link = ("Ver os modelos do TokenHarbor", "https://tokenharbor.ai/models"),
                    Copy = ("Copiar os modelos padrão", "deepseek-v4.1-flash:free, mimo-v2.6-flash:free"),
                },
                new("Acompanhe a cota", "Em Uso e cotas aparecem o quanto da cota gratuita já foi usado e quando ela renova. O período é de 7 dias, contados a partir do primeiro uso."),
            ])
        {
            Warnings =
            [
                "Com os modelos gratuitos ativados, o TokenHarbor pode guardar os pedidos e as respostas, e a equipe dele pode lê-los. Isso inclui trechos do seu currículo. Se não quiser, deixe a chave em branco: o app funciona sem o TokenHarbor.",
            ],
            Troubleshooting =
            [
                ("Uso e cotas mostra “Modelos desativados no plano.”", "Ative os modelos gratuitos no painel do TokenHarbor."),
                ("Uso e cotas mostra “Falha ao consultar o TokenHarbor”", "Confira a chave e a sua conexão."),
                ("O erro continua mostrando o Gemini ou a OpenAI", "É esperado quando o TokenHarbor também falha ou quando o problema é a chave principal. Corrija a chave principal primeiro."),
            ],
        },

        new(Apify, "Apify (LinkedIn e Indeed)",
            "O Apify executa os “actors”, programas que fazem as buscas. Você usa a sua própria conta, e os gastos são dela.",
            [
                new("Crie a conta", "Veja no site o que o seu plano inclui de crédito mensal e quanto custa o uso.") { Link = ("Abrir o Apify", "https://apify.com/") },
                new("Copie o token", "No Console, abra Settings › API & Integrations e copie o token pessoal da API.")
                    { Link = ("Abrir API & Integrations", "https://console.apify.com/settings/integrations") },
                new("Cole no app", "Em Configurações › Contas e chaves › Apify e Adzuna, cole em “Apify token” e clique em “Salvar”. As chaves da Adzuna são opcionais e só valem para a fonte Adzuna."),
                new("Ligue a fonte", "Em Configurações › Busca › Fontes de vagas, marque “LinkedIn (via Apify)”. Ela não vem marcada. Essa opção liga todos os actors ativos, inclusive o do Indeed."),
                new("Defina o teto de gasto", "“Limite mensal do Apify (USD)” começa em 5. O valor é comparado com o gasto do ciclo da sua conta inteira, não só o do Odradek. Ao chegar nele, o app para de iniciar buscas; com 0, não usa o Apify. A busca que já está rodando pode passar um pouco do teto."),
                new("Prepare a busca", "O LinkedIn precisa do filtro principal. Preencha também a cidade em Configurações › Perfil: sem ela, a busca de vagas presenciais e híbridas na sua cidade é pulada.") { Next = Filters },
                new("Colete e confira", "Clique em “Iniciar coleta”. Veja o andamento em Logs e o gasto do ciclo em Uso e cotas."),
            ])
        {
            Warnings =
            [
                "Os dois actors que vêm ligados cobram por resultado: pelas páginas deles, a partir de cerca de US$ 1 (LinkedIn) e US$ 3 (Indeed) a cada 1.000 vagas. Os preços mudam: confira na Apify Store. Para gastar menos, reduza “Vagas por busca (padrão)” ou desligue um actor.",
            ],
            Troubleshooting =
            [
                ("“Configure o token da Apify no painel.”", "Salve o token em Configurações › Contas e chaves."),
                ("HTTP 401", "O token está errado ou foi trocado no Apify. Copie o atual e salve de novo."),
                ("“Limite mensal de créditos Apify atingido”", "Espere o próximo ciclo ou aumente o limite em Fontes de vagas."),
                ("“Sem filtro Principal: nenhum run LinkedIn será iniciado.”", "Gere o filtro principal."),
                ("“Apify (…) encerrada com status …”", "A execução falhou ou passou de 3 minutos sem terminar. Abra a execução no Console do Apify para ver o motivo."),
            ],
        },

        new(Filters, "Filtros de busca",
            "Um filtro tem uma ou duas frases com o nome do mesmo cargo, mais os níveis de experiência. É com ele que o app monta as buscas no LinkedIn.",
            [
                new("Antes de gerar", "Você precisa da chave de IA salva e de um currículo já analisado."),
                new("Gere o principal", "Em Configurações › Busca › Filtro principal, clique em “Gerar com IA”. O filtro é a busca da área em que você tem mais experiência."),
                new("Confira o resultado", "A tela mostra as frases e o motivo da escolha. O app só aceita frases apoiadas no currículo; no principal, elas precisam se basear em um cargo seu, e nomes de tecnologias são recusados. O que foi descartado aparece em Logs."),
                new("Filtro secundário (opcional)", "Serve como área de reserva. Se quiser indicar um rumo, escreva em “Dica da área para a IA” antes de gerar. Ele não repete as frases do principal."),
                new("Refazer", "O filtro só muda quando você clica em “Gerar de novo com IA”, e só o daquela seção é trocado. Depois de atualizar o currículo, gere de novo."),
                new("Quanto vai para cada filtro", "Com os dois filtros, “Fatia do filtro principal (%)”, em Avançado, divide a busca: aceita de 50 a 95 e começa em 70."),
                new("Quão antigas são as vagas", "“Idade máxima das vagas do LinkedIn (dias)” vai de 1 a 30. O app busca primeiro as das últimas 24 horas, depois as de até 7 dias e por fim até o máximo escolhido."),
                new("Onde buscar", "Os locais vêm do país e da cidade do seu Perfil. “Países preferidos” são buscados primeiro (até 5, com uma cidade opcional entre parênteses). Marque “Worldwide” para incluir vagas remotas de qualquer país. “Respeitar elegibilidade geográfica” barra as vagas que não servem de onde você mora.")
                    { Copy = ("Copiar um exemplo de países", "Brazil (Belo Horizonte), Portugal") },
            ])
        {
            Troubleshooting =
            [
                ("“Analise um currículo antes de gerar o filtro…”", "Envie o currículo em Currículos. Se já enviou, espere a análise terminar em Filas."),
                ("“Configure a chave da API de IA para gerar o filtro…”", "Salve a chave do provedor escolhido em Contas e chaves."),
                ("“A IA não retornou uma frase com evidência válida no currículo”", "Gere de novo. Se repetir, veja em Logs por que as frases foram descartadas e confira se o currículo descreve os seus cargos."),
            ],
        },

        new(Actors, "Actors do Apify (avançado)",
            "A lista em Configurações › Busca › Avançado › “Actors do Apify (JSON)” diz quais actors rodam e o que cada um recebe. O padrão já funciona: só mexa para ajustar quantidades, desligar um actor ou adicionar outro.",
            [
                new("Os dois que vêm prontos", "LinkedIn Jobs (curious_coder~linkedin-jobs-scraper) e Indeed (misceres~indeed-scraper). Copie o padrão para voltar a ele quando quiser: cole no campo, no lugar do texto atual, e salve.")
                    { Copy = ("Copiar a lista padrão", ApifyActors.DefaultsJson()) },
                new("Campos de cada actor", "“id”: dono~nome (dono/nome também vale).\n“label”: o nome que aparece nos Logs.\n“enabled”: false desliga sem apagar.\n“count”: vagas desse actor por coleta, de 1 a 100. Sem ele, vale “Vagas por busca (padrão)”.\n“input_mode”: linkedin_search ou custom.\n“input_template”: só no modo custom."),
                new("Modo linkedin_search", "O app monta os endereços de busca a partir dos filtros e os envia em “urls”. Use só com actors que aceitam endereços de busca do LinkedIn nesse formato."),
                new("Modo custom", "Você escreve a entrada do actor em “input_template”, e o app troca os marcadores abaixo:\n{{keyword}} e {{location}}: o cargo e o local da execução.\n{{country_code}}: o país em duas letras (BR, US…).\n{{count_number}}: a quantidade como número; precisa ser o valor inteiro do campo.\n{{count}}: a quantidade como texto.\n{{keywords}} e {{locations}}: até 3 itens dos campos “Palavras-chave” e “Locais extras”, separados por vírgula.\n{{max_age_days}}: a idade máxima em dias."),
                new("Quantas vezes um actor custom roda", "Se o modelo usa {{keyword}} ou {{location}}, há uma execução para cada cargo em cada local, no máximo 6, e o “count” é dividido entre elas. Sem esses marcadores, ele roda uma vez. Os cargos vêm dos filtros (sem filtro, de “Palavras-chave”). São até 3 locais, nesta ordem: “Locais extras dos actors personalizados”, remote, país e cidade."),
                new("Adicionar um actor", "Escolha um na Apify Store e veja o preço. Na aba Input, mude para JSON para ver os nomes exatos dos campos. Copie o modelo abaixo para a lista (separando os actors por vírgula) e ajuste o id e os nomes dos campos.")
                {
                    Link = ("Abrir a Apify Store", "https://apify.com/store"),
                    Copy = ("Copiar um modelo de actor", "{\"id\": \"dono~nome-do-actor\", \"label\": \"Meu actor\", \"enabled\": true, \"input_mode\": \"custom\", \"count\": 5, \"input_template\": {\"position\": \"{{keyword}}\", \"location\": \"{{location}}\", \"maxItems\": \"{{count_number}}\"}}"),
                },
                new("Teste com pouco", "Comece com um “count” baixo, salve, inicie a coleta e confira em Logs se vieram vagas úteis e, em Uso e cotas, quanto custou. Um actor que devolve os dados num formato que o app não reconhece pode cobrar e não gerar nenhuma vaga."),
            ])
        {
            Troubleshooting =
            [
                ("“JSON de actors Apify inválido” ou “deve ser uma lista JSON”", "Confira aspas, vírgulas e colchetes. A lista começa com [ e termina com ], mesmo com um actor só."),
                ("“em modo custom precisa de input_template”", "Acrescente “input_template” ou mude o “input_mode”."),
                ("“Nenhum actor Apify habilitado”", "Ligue pelo menos um com \"enabled\": true."),
                ("A execução falha logo no início", "Quase sempre é um campo com nome ou tipo diferente do que o actor espera. Compare com a aba Input dele."),
            ],
        },
    ];

    public static Guide Get(string id) => All.FirstOrDefault(g => g.Id == id) ?? All[0];
}
