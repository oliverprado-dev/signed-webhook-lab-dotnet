# signed-webhook-lab-dotnet

Laboratório em C# para receber eventos fictícios `order.paid` no ASP.NET Core. A API verifica o HMAC-SHA256 sobre os bytes originais da entrega e registra o evento e seu lançamento de pagamento em uma transação SQLite. Reenvios autenticados do mesmo evento preservam um único lançamento, inclusive em entregas concorrentes e após reiniciar a API.

## Requisitos

- Linux com o SDK **.NET 10 LTS 10.0.403**, fixado em [global.json](global.json). [Download oficial do .NET 10](https://dotnet.microsoft.com/pt-br/download/dotnet/10.0)
- Bash, `openssl` para gerar a chave e `sqlite3` para consultar o banco.

Execute os comandos a partir da pasta que contém `signed-webhook-lab-dotnet.sln`.

## 1. Gerar a chave local

```bash
umask 077
chave_webhook=$(openssl rand -hex 32) &&
  printf 'WEBHOOK_SECRET=%s\n' "$chave_webhook" > .env
unset chave_webhook
```

Gere o arquivo uma vez e use a mesma chave nos dois terminais. O arquivo [.env.example](.env.example) mostra o formato com um valor público. Os comandos abaixo exportam a variável a partir do arquivo local.

## 2. Iniciar a API

No primeiro terminal, mantenha a API em execução.

```bash
set -a
source ./.env
set +a
export WEBHOOK_DB_PATH="$PWD/data/webhooks.db"
dotnet restore signed-webhook-lab-dotnet.sln -m:1 --locked-mode
dotnet build signed-webhook-lab-dotnet.sln -m:1 -c Release --no-restore
dotnet run --project src/SignedWebhook.Api -c Release --no-build --no-restore
```

A API atende em `http://127.0.0.1:5080` por padrão. O endpoint é `POST /webhooks/orders`. A aplicação cria o diretório e o arquivo SQLite quando necessário. Sem `WEBHOOK_DB_PATH`, ela usa `data/webhooks.db` relativo ao diretório de trabalho do processo.

## 3. Enviar um evento, adulterar o corpo e reenviar

No segundo terminal, na mesma pasta do projeto, execute o simulador.

```bash
set -a
source ./.env
set +a
evento=4f66dbe4-f57f-40f4-ab8d-9cd9fc97775b
dotnet run --project src/SignedWebhook.Simulator -c Release --no-build --no-restore -- "$evento"
```

Para um ID novo, os códigos esperados são `201` no evento válido, `401` no corpo adulterado com a assinatura original e `200` no reenvio válido. O simulador mantém o ID e os bytes do corpo no reenvio e exibe o código de cada resposta. Se você omitir o UUID, ele gera um novo. `WEBHOOK_URL` permite indicar outra URL de loopback.

## 4. Conferir o SQLite

```bash
sqlite3 -readonly -header -column data/webhooks.db <<'SQL'
SELECT event_id, order_id, amount_cents, currency
FROM payments ORDER BY event_id;

SELECT
  (SELECT count(*) FROM received_events) AS eventos,
  (SELECT count(*) FROM payments) AS lancamentos;
SQL
```

Em um banco novo, a sequência acima deixa **1 evento e 1 lançamento** para o pedido fictício `ord-1042`, com `25990` centavos em `BRL`.

## 5. Repetir após reiniciar e testar concorrência

1. Pare a API com `Ctrl+C`. Repita o comando `dotnet run` da etapa 2, mantendo a chave e o caminho do banco.
2. Execute novamente o simulador com `"$evento"` no segundo terminal. A nova execução assina o mesmo ID e corpo com o horário atual. A sequência esperada é `200`, `401`, `200`. A consulta deve continuar mostrando um evento e um lançamento.
3. Em seguida, use um UUID novo para a demonstração concorrente.

```bash
concorrente=1a70c3db-c9c5-4409-9869-4cf373b7a5fb
dotnet run --project src/SignedWebhook.Simulator -c Release --no-build --no-restore -- --concurrent "$concorrente"
```

Com o segundo ID ainda inédito, as duas respostas devem ser `201` e `200`, em qualquer ordem. A consulta deve mostrar dois eventos e dois lançamentos ao todo, um para cada ID.

## Testes e integração contínua

```bash
dotnet restore signed-webhook-lab-dotnet.sln -m:1 --locked-mode
dotnet build signed-webhook-lab-dotnet.sln -m:1 -c Release --no-restore
dotnet test signed-webhook-lab-dotnet.sln -m:1 -c Release --no-build --no-restore
```

Os testes usam Kestrel em portas locais temporárias, um relógio controlado e um arquivo SQLite temporário por caso. Os arquivos `packages.lock.json` fixam a resolução das dependências diretas e transitivas. O [workflow](.github/workflows/ci.yml) executa os mesmos estágios no Ubuntu a cada push e pull request.

## Estrutura

| Caminho | Conteúdo |
|---|---|
| `src/SignedWebhook.Api` | Endpoint, validação e transação SQLite |
| `src/SignedWebhook.Protocol` | Constantes e cálculo do HMAC |
| `src/SignedWebhook.Simulator` | Envio válido, adulteração, reenvio e concorrência |
| `tests/SignedWebhook.IntegrationTests` | Casos HTTP, verificações no SQLite e vetor HMAC |
| [docs/relatorio-seguranca.md](docs/relatorio-seguranca.md) | Protocolo, evidências e escopo da garantia |

## Dependências de terceiros

Os pacotes são restaurados do NuGet e regidos pelas licenças dos respectivos projetos. [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.14) · [ASP.NET Core MVC Testing](https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing/10.0.14) · [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1) · [xUnit](https://www.nuget.org/packages/xunit/2.9.3) · [xUnit Visual Studio Runner](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5)

O provedor SQLite carrega a biblioteca nativa por meio de SQLitePCLRaw. [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) · [Licença do SQLite](https://sqlite.org/copyright.html)
