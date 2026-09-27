# Coppertop

Automated Kraken crypto trader. Three fully isolated backend services plus a web front end, with no shared code. They talk to each other only through JSON over HTTP.

| Service | Path | Purpose | Spends tokens? |
|---|---|---|---|
| **Api** | `src/Coppertop.Api` | The only service that writes to SQLite: opportunities, vetoes, positions, orders, trades, token usage and the summary. | No |
| **Trader** | `src/Coppertop.Trader` | Runs a plain C# loop every 30s: reads opportunities, checks prices, enters positions and manages exits. | No |
| **Research** | `src/Coppertop.Research` | Publishes opportunities (what to buy and within which limits). Currently a **stub**. | Later |
| **Web** | `src/Coppertop.Web` | React dashboard: summary, open positions, opportunities, orders, trades, closed positions and token usage. Refreshes every 10s. | No |

## How Research tells the Trader what to buy
1. Research sends `POST /opportunities` with `{ asset, strategy:"dip", maxEntryPrice, takeProfitPct, stopLossPct, maxSpendUsd, confidence, reason, expiresAt }`. A new opportunity replaces the previous one for the same asset and strategy.
2. Research can send `POST /vetoes` to block an asset. This cancels its active opportunities and blocks new ones until the veto expires.
3. The Trader polls `GET /opportunities?active=true`. It enters only when the price is below the dip band (moving average of the last N closes, SMA, − k·σ), the price is ≤ `maxEntryPrice`, the take-profit covers the fees, and the risk checks pass.
4. The Trader claims an opportunity with `POST /opportunities/{id}/consume`. Only one consume can succeed per opportunity.

## Run with Docker (or Podman)
```bash
docker compose up --build        # or: podman compose up --build
```
- Dashboard: http://localhost:8080
- Api: http://localhost:5057
- The SQLite database is kept in the `coppertop-data` volume.

Optional secrets go in `./.env` (git-ignored) or the shell:
- `COPPERTOP_API_KEY` turns on the key check between services. nginx adds the key to requests on the server side, so the browser never sees it.
- `KRAKEN_API_KEY` and `KRAKEN_API_SECRET` turn on Kraken's `validate=true` order checks.

Each service has its own `Dockerfile` in its folder and can be built on its own, e.g. `docker build src/Coppertop.Trader`. The Dockerfiles cross-compile for the target architecture (`--platform=$BUILDPLATFORM` + `dotnet publish -a $TARGETARCH`), so building arm64 images on an x64 machine is fast.

## CI / Raspberry Pi
Each service has its own workflow in `.github/workflows/` (`api.yml`, `trader.yml`, `research.yml`, `web.yml`).
- A workflow runs on a push to `main` that touches that service's folder, or when started manually.
- It builds `linux/amd64` and `linux/arm64` images and pushes them to `ghcr.io/<owner>/coppertop-<service>`, tagged `latest` and with that workflow's `github.run_number`.
- The Api and Trader workflows run their unit tests first.

On the Pi:
```bash
docker compose -f compose.pi.yaml pull && docker compose -f compose.pi.yaml up -d
TRADER_TAG=42 docker compose -f compose.pi.yaml up -d trader   # pin/roll back one service
```
New GHCR packages are private by default. Either run `docker login ghcr.io` on the Pi with a personal access token that has `read:packages`, or make each package public.

## Run locally
```bash
cd src/Coppertop.Api      && dotnet run   # http://localhost:5057
cd src/Coppertop.Research && dotnet run   # publishes stub opportunities, then repeats hourly (retries after 1 min on failure)
cd src/Coppertop.Trader   && dotnet run   # paper-trades every 30s
cd src/Coppertop.Web      && npm install && npm run dev   # http://localhost:5173, forwards /api to :5057
dotnet test src/Coppertop.Api.Tests
dotnet test src/Coppertop.Trader.Tests
```
In dev, set `COPPERTOP_API_URL` and `COPPERTOP_API_KEY` (env vars or `src/Coppertop.Web/.env.local`) if the Api isn't on :5057 or has a key.
Quick checks: `curl localhost:5057/summary`, `curl "localhost:5057/positions?status=open"`.

## Safety
- The Trader only runs in **Paper** mode. Any other `Trader:Mode` makes it refuse to start.
- If Kraken credentials are set (`Kraken__ApiKey`, `Kraken__ApiSecret`, or user-secrets), the Trader sends each entry to Kraken with `validate=true`. Kraken checks the order and places nothing. Fills are always simulated.

## Paper fill model
Paper fills are deliberately pessimistic, so paper P&L shouldn't flatter the strategy:
- **Entries rest.** A buy is recorded as an `open` order at the bid; no position exists yet. It fills (at the limit) only when a Kraken public trade prints *strictly below* the limit after placement, or the ask drops below it. A print exactly at our price doesn't count, because we don't know our place in the queue.
- **Pending buys reserve cash** and count toward max positions and per-asset exposure.
- **Unfilled buys are cancelled** after `EntryOrderTimeoutMinutes` (15), when the bid runs `EntryOrderRunawayPct` (0.5%) above the limit, or when the asset is vetoed. The next Research run can re-approve the coin.
- **Take-profit** is a resting maker sell. It fills at the TP price only when a trade prints strictly above it.
- **Stop-loss** is a taker market sell once price touches the stop. It fills at min(stop, bid) less `StopLossSlippagePct` (0.1%).
- If both the take-profit and the stop were reached in the same window, whichever happened first wins, and ties go to the stop.
- Kraken's `Trades` endpoint is read incrementally per asset (`TradeTape`), so brief wicks between 30s cycles aren't missed.
- The Api records fills atomically: `POST /orders/{id}/fill` opens or closes the position, records the trade and links the order. `POST /orders/{id}/cancel` only succeeds on open orders.
- Setting `Api:Key` on the Api, and `CoppertopApi:ApiKey` on the other two services, turns on the `X-Api-Key` header check.

## Known limitations / next steps
- Research is a stub: it approves every watchlist asset below its last price. Next: TypeSafe AI for the decision logic, Sonar for news vetoes, and token usage recorded through `POST /token-usage`.
- Live mode: Kraken supports only one conditional close order per entry, so the take-profit sits on Kraken and the Trader must watch the stop-loss itself.
- The dashboard has no live prices yet, so it can't show unrealized P&L. It also has no kill switch.
- Prices are polled over REST. Switch to WebSocket if polling is too slow.
- Money columns are SQLite `REAL`, so they aren't exact to the cent. Switch to integer cents or TEXT if exactness matters.
