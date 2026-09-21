# BFA NET — Backend (.NET 9)

API REST `/api/v1` sobre **PostgreSQL** (EF Core + Npgsql), minimal APIs, autenticação JWT.

## Arquitectura

Quatro projectos em camadas; as setas são referências de projecto (a dependência aponta sempre para dentro).

```
BfaNet.Api  ──►  BfaNet.Infrastructure  ──►  BfaNet.Application  ──►  BfaNet.Domain
(HTTP)           (EF, cripto, serviços)      (contratos, regras)       (entidades, razão)
```

| Projecto | Responsabilidade | Conteúdo |
|---|---|---|
| **Domain** | Modelo puro, sem dependências | `Entities/*`, `Enums.cs`, `Ledger/LedgerPosting` (invariante de partida dobrada), `DomainException` |
| **Application** | O *quê* (sem EF nem HTTP) | `Contracts` (DTOs), `Abstractions` (interfaces dos serviços e da segurança), `Validators` (FluentValidation), `Common` (`Patterns`, `TextSanitizer`, `Iban`, `CredentialPolicy`, `AppException`, `SystemAccounts`), `Options` |
| **Infrastructure** | O *como* | `Persistence` (DbContext, migrações, seed), `Security` (AES-GCM, HMAC, Argon2id, JWT), `Services` (casos de uso), `DependencyInjection` |
| **Api** | Fronteira HTTP | `Endpoints`, `Http/*` (middleware, erros, rate limiting, cookies), `Program.cs`, `appsettings*` |
| **Tests** | xUnit | unitários + integração (`WebApplicationFactory` sobre Postgres real) |

### Pipeline de um pedido
`ForwardedHeaders → ExceptionHandler (RFC 7807) → cabeçalhos de segurança → HSTS (fora de dev) → RateLimiter → CSRF → Authentication (JWT do header **ou** do cookie) → Authorization → endpoint → filtro de validação (FluentValidation) → serviço`.

- **Erros**: `AppException(code, message, status, errors?)` é a falha esperada e segura para mostrar; qualquer outra excepção vira 500 genérico com `traceId` (detalhe só no log).
- **Validação**: cada `Request` tem um validator; falhas → 422 com `errors` por campo (nomes em camelCase = nomes dos campos do formulário).
- **JSON**: enums como texto; **campos desconhecidos são rejeitados** (sem *mass assignment*).

## Modelo de dados

```
customers ─┬─< accounts ─< ledger_entries >─ transactions
           ├─< beneficiaries        (customer_id, iban únicos)
           ├─< cards                (só últimos 4 dígitos; nunca PAN/CVV/PIN)
           ├─< refresh_tokens       (família + hash SHA-256)
           ├─< device_credentials   (login biométrico, hash SHA-256, revogável)
           ├── customer_avatars     (1:1, JPEG já processado, ≤ 300 KB)
           ├─< loans ─< loan_installments   (microcrédito; 1 activo por cliente — índice único parcial)
           └─< audit_logs           (append-only, sem segredos)
exchange_rates
```

- **Razão de partida dobrada**: cada movimento é um conjunto equilibrado de `ledger_entries` (débitos = créditos). Comissões e liquidações vão para **contas internas** (`SystemAccounts`: cofre, liquidação interbancária/serviços/carregamentos/Estado, comissões).
- **Saldos** só mudam por `UPDATE accounts SET balance = balance ± x … WHERE balance >= x RETURNING balance`, com as pernas ordenadas por id (sem *lost update*, sem descoberto, sem *deadlock*) + `CHECK (balance >= 0 OR type='Interna')`. Um *lock* de linha do cliente serializa verificações de limite e idempotência.
- **Índices**: *blind indexes* únicos (email/telefone/BI), IBAN e nº de conta únicos, extracto por cursor `(account_id, id DESC)`, intervalo de datas `(account_id, created_at)`, idempotência `UNIQUE(initiated_by, idempotency_key)`, limite diário `(initiated_by, created_at DESC)`, índice parcial de *refresh tokens* vivos.
- **CHECK**: formato IBAN/BI/últimos 4, montantes positivos, saldos não negativos, tamanho do avatar.
- Migrações em `Infrastructure/Persistence/Migrations` (`InitialCreate`, `AddMobileFeatures`, `AddAssistant`); aplicadas no arranque **apenas em Development**.

## API

| Área | Endpoints |
|---|---|
| Público | `GET /public/exchange-rates` · `/public/products` · `/public/contacts` · `/public/about` · `GET /health/live` · `/health/ready` |
| Auth | `POST /auth/register` · `/login` · `/refresh` · `/logout` · `/biometric/login` |
| Auth (sessão) | `POST /auth/logout-all` · `/change-password` · `/change-pin` · `/verify-pin` · `/biometric/enroll` · `/biometric/disable` · `GET /auth/sessions` · `DELETE /auth/sessions/{id}` |
| Perfil | `GET /me` · `GET · PUT · DELETE /me/avatar` (PUT = `multipart/form-data`, campo `file`) |
| Contas | `GET /accounts` · `GET · PATCH /accounts/{id}` · `GET /accounts/{id}/statement?from&to&direction&cursor&limit` · **`GET /accounts/{id}/statement.pdf?from&to`** |
| Dinheiro | `POST /transfers/resolve-iban` · `/transfers` · `/transfers/kwik/resolve` · `/transfers/kwik` · `/payments/services` · `/payments/recharges` · `/payments/state` · `GET /transactions/{id}` · **`GET /transactions/{id}/receipt.pdf`** |
| Beneficiários | `GET · POST /beneficiaries` · `DELETE /beneficiaries/{id}` |
| Cartões | `GET /cards` · `PATCH /cards/{id}` |
| **Assistente** | `GET /assistant/insights` · `GET /assistant/credit` · `GET /assistant/credit/simulate?amount&months` · `POST /assistant/credit/accept` · `GET /assistant/loans` · `POST /assistant/loans/{id}/repay` |

Operações que movem dinheiro exigem `Idempotency-Key: <uuid>` e `pin`. Repetir a mesma chave com o mesmo corpo devolve o recibo original; com corpo diferente → 422 `idempotency_mismatch`.

### Assistente financeiro (`Application/Assistant`)
Estatística **determinística e explicável** sobre os próprios movimentos do cliente (não usa IA/LLM; nada sai do servidor). Núcleo puro e testável, sem acesso à BD:
- `SpendCategorizer` — categoria por palavras-chave (prefixo de palavra: «Prenda» não é «renda»); `SpendingAnalyzer` — fluxos mensais, quota por categoria, **previsão de fim de mês** (ritmo actual combinado com a média anterior), pagamentos **recorrentes** (intervalo 24–38 dias, variação do valor ≤ 20 %; a próxima data nunca fica no passado), subidas por categoria, despesa fora do normal face ao histórico *da própria categoria*, poupança sugerida e **pontuação de saúde 0–100** (poupança 35 · reserva de emergência 25 · estabilidade 20 · regularidade do rendimento 20).
- `CreditScoring` + `LoanMath` — elegibilidade **com motivos e bloqueios** (≥ 30 dias de histórico, rendimento em ≥ 2 dos últimos 3 meses e ≥ 30 000 Kz, saúde ≥ 40, sem crédito activo); prestação ≤ 30 % do rendimento; máximo = mín(1 000 000 Kz, capital que a prestação comporta ao prazo mais longo, 3× rendimento); taxa 18/24/30 % consoante a saúde; comissão 1 %; prazos 3/6/9/12 meses (anuidade). Parâmetros em `AssistantOptions` (`Assistant:*`).
- **Aceitar** (`POST /credit/accept`): PIN + `Idempotency-Key`, *lock* de linha do cliente, recalcula a oferta no servidor (o cliente nunca dita taxa nem limite) e lança na razão: débito `CreditPortfolio`, crédito na conta (valor − comissão) e nas comissões. Duplo pedido em paralelo → um só desembolso (índice único parcial). **Pagar prestação** debita a conta e amortiza a próxima.
- Simulação de demonstração: não há avaliação de crédito real nem bureau.

### Regras de negócio
- **Transferência por IBAN**: IBAN do BFA (`AO06 0006…`) → conta interna existente; outro banco → nome do beneficiário obrigatório e comissão fixa (`Banking:InterbankFee`, 150 Kz).
- **KWiK**: a chave é o telemóvel; o destino é a primeira conta activa não-poupança do titular (pesquisa pelo *blind index*). Não é possível enviar a si próprio. A resolução devolve só o nome **mascarado**.
- **Recargas** (`Unitel`, `Africell` → telemóvel 9XXXXXXXX, 100–50 000 Kz; `Dstv`, `Zap`, `Ende` → nº de 9–12 dígitos, 100–500 000 Kz), **Estado** (referência de 13 dígitos), **serviços** (entidade 5 + referência 9).
- **Limites**: por operação 5 000 000 Kz e diário 10 000 000 Kz (dia em hora de Luanda, UTC+1); só conta saídas iniciadas pelo cliente.
- **Bloqueios**: 5 palavras-passe erradas → 15 min; 3 PIN errados → 30 min (contadores por `UPDATE` atómico).
- **Rate limits** (por minuto): global 240 (utilizador ou IP), credenciais 10 por IP, dinheiro/avatar 15 por utilizador — configuráveis em `RateLimits`.

## Segurança

- **Cifra em repouso**: `AesGcmFieldEncryptor` (envelope `v1.<keyId>.<nonce|tag|cifra>`, AAD = nome da coluna); conversores de valor no `BankDbContext`. Rotação: acrescentar chave e mudar `ActiveEncryptionKeyId`; as antigas continuam a decifrar.
- **Sessões**: `TokenService` (JWT HS256 com `ValidAlgorithms` fixo, só ids opacos nos *claims*). Refresh rotativo; token já rodado reaparecendo depois de 10 s revoga a família. Tecto absoluto de 30 dias.
- **Web (cookies)**: `bfa_at` (Path `/api`), `bfa_rt` (Path `/api/v1/auth`), `bfa_sess` (Path `/`, sem conteúdo — só para o `proxy.ts` do Next). **CSRF**: pedidos com cookie exigem `X-BFA-Client: web` e `Origin` na lista `Security:AllowedOrigins`.
- **Mobile**: `X-BFA-Client: mobile` → tokens no corpo; Bearer nos pedidos.
- **Biometria**: `device_credentials` — token aleatório de 48 bytes por dispositivo, guardado só como SHA-256, máximo 5 por cliente, revogado ao mudar a palavra-passe/`logout-all`. Inscrever exige a palavra-passe. `verify-pin` permite guardar o PIN atrás do sensor no telemóvel (o servidor continua a validar o PIN em cada operação).
- **Uploads (avatar)** — tudo no servidor (`AvatarService` + `AvatarImageProcessor`, **SkiaSharp**): tipo pelos *magic bytes* (JPEG/PNG/WebP, nunca pelo cabeçalho), ≤ 10 MB, cabeçalho lido antes de descodificar (lado ≤ 8000 px e ≤ 30 MP contra *decompression bombs*), aplica a orientação EXIF, recorte quadrado central, 512×512, fundo branco para transparência, JPEG ≤ 300 KB. O ficheiro guardado é sempre **píxeis re-codificados**: sem EXIF/GPS, ICC, animação ou *payloads* escondidos.
- **Comprovativo PDF** (`ReceiptPdfService`, **QuestPDF**): gerado no servidor, com o **logo do BFA no topo esquerdo**, montante/estado, **Ordenante** (nome, nº de adesão, BI e telemóvel mascarados), **conta debitada/creditada** (tipo e IBAN), destino, comissão, data (hora de Luanda) e referência. **Privacidade**: os dados de identificação do ordenante só aparecem ao próprio ordenante — quem recebe vê apenas o nome do ordenante e a sua própria conta; o saldo nunca é impresso. Só quem participou na transação o obtém (senão 404). Fonte **Liberation Serif** (SIL OFL) embebida no assembly, porque o QuestPDF ignora as fontes do sistema — o PDF é igual em qualquer servidor. Licença QuestPDF *Community* (gratuita para projectos open source e empresas com receita anual < 1 M USD; acima disso requer licença comercial).
- **Extracto PDF** (`StatementPdfService`): logo, titular (nº de adesão, BI mascarado), conta, período, **saldo inicial/final** (do razão), entradas e saídas do período inteiro, contagem e tabela paginada (cabeçalho repetido, linhas nunca partidas entre páginas, "Página x de y"). Período por omissão = mês corrente; `from ≤ to ≤ hoje` (hora de Luanda) e no máximo 366 dias; tabela limitada aos 1000 movimentos mais recentes (os totais contam todos). É o único sítio onde os movimentos levam sinal (+/−); o **comprovativo** mostra só o valor e o tipo de operação.
- **IDOR**: toda a consulta é filtrada pelo chamador; ids alheios dão o mesmo 404.
- **Auditoria**: `AuditWriter` usa contexto próprio (sobrevive ao *rollback* de negócio); nunca grava segredos nem PII em claro.
- **Registo**: erro genérico em duplicados (sem enumeração de utilizadores).

## Configuração

Ordem de precedência: variáveis de ambiente › `user-secrets` › `appsettings.Local.json` (só Development, git-ignored) › `appsettings.{Env}.json` › `appsettings.json`.

| Chave | Notas |
|---|---|
| `ConnectionStrings:Default` | obrigatória |
| `Security:EncryptionKeys:k1` (base64, 32 B) · `ActiveEncryptionKeyId` | obrigatória |
| `Security:BlindIndexKey` · `Security:JwtSigningKey` (base64, ≥ 32 B) | obrigatórias |
| `Security:SecureCookies` | **tem de ser `true` fora de Development** (a app recusa arrancar) |
| `Security:AllowedOrigins` | origens do browser para o CSRF (por omissão `https://bfa-net.josemarsilva.me`; em dev, `appsettings.Development.json` usa `http://localhost:3000`) |
| `Banking:*` · `RateLimits:*` · `TrustedProxies` | ver `appsettings.json` |
| `Seed:Demo` · `Seed:DemoPassword` · `Seed:DemoPin` | só Development. O cliente demo traz ~100 dias de histórico (salário, renda, propinas, DStv, ENDE, compras, comissões interbancárias…) para o assistente; **só é criado com a BD vazia** — para o obter numa BD existente, recriá-la |
| `Assistant:*` | limites e taxas do microcrédito (`AssistantOptions`) |

`appsettings.Development.json` traz **chaves descartáveis só para desenvolvimento local**; em produção tudo vem de variáveis de ambiente/cofre. O `ExchangeRateRefresher` tenta actualizar o EUR a partir de bfa.ao a cada 6 h (com validação de sanidade; em dúvida mantém o valor anterior).

## Correr

```bash
cp src/BfaNet.Api/appsettings.Local.json.example src/BfaNet.Api/appsettings.Local.json   # credenciais do teu Postgres
dotnet run --project src/BfaNet.Api        # http://localhost:5080 · migra e semeia em Development
curl localhost:5080/health/ready
```

Migrações novas: `dotnet ef migrations add <Nome> -p src/BfaNet.Infrastructure -s src/BfaNet.Api -o Persistence/Migrations`.
Nota: `dotnet run` na pasta `backend/` falha porque aí só existe a solução — indicar sempre `--project src/BfaNet.Api`.

## Testes

`dotnet test` corre os unitários. Definindo `BFANET_TEST_CONNECTION` (BD **descartável**) corre também a integração: idempotência, 20 transferências concorrentes (exactamente 10 aceites, saldo final certo), bloqueio de PIN/login, reutilização de refresh token, PII cifrada em repouso, CSRF, IDOR, recargas/Estado/KWiK, avatar (imagens reais geradas com SkiaSharp: recorte, formatos, transparência, bombas, falsificações, tamanho), biometria e `verify-pin`. Os dados de teste são aleatórios para várias classes partilharem a mesma BD sem colisões.
