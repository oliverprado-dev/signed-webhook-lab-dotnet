# Relatório de segurança

## Protocolo v1

O emissor envia um evento `order.paid` em UTF-8 por `POST /webhooks/orders` e informa cada cabeçalho uma vez.

| Cabeçalho | Formato |
|---|---|
| `X-Webhook-Id` | UUID no formato `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` |
| `X-Webhook-Timestamp` | Timestamp Unix em segundos, em decimal ASCII sem sinal, com até 19 dígitos e dentro do intervalo de `Int64` |
| `X-Webhook-Signature` | HMAC-SHA256 de 32 bytes em 64 caracteres hexadecimais, em maiúsculas ou minúsculas |

A entrada do HMAC é a concatenação abaixo. `\n` representa um byte LF (`0x0A`).

```text
UTF8("v1\n{timestamp}\n{id}\n") || bytes_originais_do_corpo
```

A chave é o valor de `WEBHOOK_SECRET` codificado em UTF-8. Uma chave gerada por `openssl rand -hex 32` é usada como texto, sem decodificar o hexadecimal. O timestamp e o UUID entram no cálculo com a grafia recebida. A API padroniza o UUID para minúsculas somente ao identificar o evento no SQLite.

A API aceita corpos de até 64 KiB (65.536 bytes). Se `Content-Length` já indicar excesso, ela encerra a entrega antes de ler o corpo. Em uma transmissão sem esse cabeçalho, o leitor usa um buffer limitado com um byte adicional para detectar excesso. A contagem usa os bytes do corpo entregues à aplicação, excluindo o enquadramento HTTP das requisições em partes (`chunked`).

Depois de decodificar os 64 caracteres hexadecimais, a API compara os 32 bytes com `CryptographicOperations.FixedTimeEquals`. Com o HMAC confirmado, aceita um timestamp entre `agora − 300 s` e `agora + 300 s`, inclusive. Só então interpreta o JSON. Quando o SQLite lança uma exceção na gravação, a API registra uma mensagem genérica e o código numérico do SQLite. A chave, a assinatura e o corpo ficam fora dos logs da aplicação.

Um novo lançamento requer um objeto JSON com quatro propriedades, sem repetição e com os nomes abaixo. `orderId` tem de 1 a 64 caracteres ASCII entre letras, dígitos, hífen e sublinhado. `amountCents` é um inteiro positivo representável em `Int64`. `currency` tem três letras ASCII maiúsculas.

```json
{"type":"order.paid","orderId":"ord-1042","amountCents":25990,"currency":"BRL"}
```

## Respostas HTTP

As respostas de sucesso incluem `status` e `eventId`. As respostas de erro incluem `error`.

| HTTP | Código no JSON | Condição |
|---|---|---|
| `201` | `recorded` | Novo evento e pagamento confirmados na transação |
| `200` | `duplicate` | Entrega autenticada com UUID já registrado e os mesmos bytes de corpo |
| `400` | `invalid_headers` | UUID ou timestamp ausente, repetido ou fora do formato |
| `400` | `invalid_payload` | Corpo inválido para um UUID inédito ou requisição truncada identificada durante a leitura |
| `401` | `invalid_signature` | Assinatura ausente, repetida, fora do formato ou incompatível com os bytes recebidos |
| `401` | `timestamp_outside_window` | Assinatura válida e timestamp além de cinco minutos, no passado ou no futuro |
| `409` | `event_id_conflict` | Entrega autenticada com UUID já registrado e corpo diferente, inclusive por espaços ou formatação |
| `413` | `body_too_large` | Corpo acima de 65.536 bytes |
| `500` | `persistence_error` | Falha do SQLite durante o registro |

## Vetor de teste

Este vetor usa uma chave pública exclusiva para reproduzir o cálculo. O corpo termina em `}`, sem quebra de linha. O timestamp histórico permite verificar o HMAC de forma reproduzível. Para enviar um evento à API, assine-o com um horário recente.

```bash
corpo='{"type":"order.paid","orderId":"ord-1042","amountCents":25990,"currency":"BRL"}'
printf 'v1\n1700000000\n123e4567-e89b-42d3-a456-426614174000\n%s' "$corpo" |
  openssl dgst -sha256 -hmac 'vetor-publico-hmac-v1'
```

Assinatura esperada e calculada com OpenSSL.

```text
17920e058f6140d1f2be8aa8aa8abcd333285efd60533a177a549234bb748701
```

## Transação e escopo

Uma transação `BEGIN IMMEDIATE` serializa os escritores do mesmo arquivo SQLite. Dentro dela, a API consulta o UUID e compara o SHA-256 dos bytes do corpo com a impressão digital armazenada. Se o ID é inédito e o JSON é válido, a API insere o evento em `received_events` e só então insere o pagamento em `payments`. A chave primária de `received_events` é o UUID. Em `payments`, o UUID é chave primária e chave estrangeira para o evento. Uma falha antes do commit desfaz ambas as gravações. [Transações do Microsoft.Data.Sqlite](https://learn.microsoft.com/pt-br/dotnet/standard/data/sqlite/transactions) · [Transações do SQLite](https://www.sqlite.org/lang_transaction.html)

A garantia do laboratório é de um lançamento por UUID confirmado no mesmo arquivo SQLite. Se o timestamp anterior sair da janela de aceitação, o emissor pode assinar o mesmo ID e os mesmos bytes com um timestamp novo. UUIDs diferentes podem registrar pagamentos para o mesmo pedido. A garantia se limita às gravações nas duas tabelas locais.

## Execução observada no Linux

Validação em 10 de novembro de 2026, em Debian 13 x86-64, com o SDK .NET `10.0.403` e o runtime ASP.NET Core `10.0.14`. A restauração em modo bloqueado e a compilação Release terminaram com sucesso, com **0 avisos e 0 erros** na compilação. O xUnit executou **30 casos**, todos aprovados.

### Simulador e processo da API

A demonstração começou com um arquivo SQLite novo. A API escutou em `127.0.0.1:5080`. Entre a primeira e a segunda sequência, o processo da API foi encerrado e iniciado novamente com o mesmo arquivo SQLite e a mesma chave.

| Execução | Respostas HTTP observadas | Eventos e lançamentos no SQLite |
|---|---|---|
| ID novo, adulteração e reenvio sequencial | `201`, `401`, `200` | 1 evento e 1 lançamento |
| Mesmo ID e corpo após reiniciar a API | `200`, `401`, `200` | 1 evento e 1 lançamento |
| Duas entregas concorrentes para um segundo ID | `201` e `200` | 2 eventos e 2 lançamentos no total |

A consulta ao SQLite retornou as seguintes linhas ao final. O lançamento do primeiro UUID preservou o valor original de `25990` centavos. O simulador havia tentado alterá-lo para `39990` na entrega adulterada.

| `event_id` | `order_id` | `amount_cents` | `currency` |
|---|---|---:|---|
| `1a70c3db-c9c5-4409-9869-4cf373b7a5fb` | `ord-1042` | 25990 | BRL |
| `4f66dbe4-f57f-40f4-ab8d-9cd9fc97775b` | `ord-1042` | 25990 | BRL |

O `PRAGMA integrity_check` retornou `ok` depois de cada etapa. A inspeção dos logs da demonstração confirmou a ausência da chave de execução e das assinaturas.

### Casos observados nos testes

A suíte enviou requisições HTTP a instâncias do Kestrel, consultou arquivos SQLite temporários e controlou o relógio para verificar os limites de tempo. O teste de concorrência reteve as duas requisições até ambas chegarem à aplicação e então liberou o processamento.

| Cenário | Resultado confirmado |
|---|---|
| Corpo adulterado com a assinatura original, inclusive quando o resultado da adulteração é JSON inválido | `401 invalid_signature` e ambas as tabelas vazias |
| Assinatura ausente, malformada ou incorreta | `401 invalid_signature` e ambas as tabelas vazias |
| Timestamp 301 segundos no passado ou no futuro | `401 timestamp_outside_window` e ambas as tabelas vazias |
| Timestamp no limite de 300 segundos em qualquer direção | `201 recorded` e um lançamento por caso |
| 65.537 bytes, com e sem `Content-Length` | `413 body_too_large` e ambas as tabelas vazias |
| Exatamente 65.536 bytes, com e sem `Content-Length` | `201 recorded` e um lançamento por caso |
| Cabeçalhos de identificação inválidos ou corpo assinado fora do formato | `400` e ambas as tabelas vazias |
| Duas entregas autenticadas simultâneas para o mesmo UUID e corpo | Um `201 recorded`, um `200 duplicate` e um lançamento |
| UUID registrado com corpo diferente e nova assinatura válida | `409 event_id_conflict` e lançamento original preservado |
| Falha forçada no INSERT do pagamento, por exceção ou por comando sem linha afetada | `500 persistence_error` e ambas as tabelas vazias. Após retirar o gatilho, a mesma entrega retornou `201 recorded` e gravou uma linha em cada tabela |
