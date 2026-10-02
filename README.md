# Coppertop

Automated Kraken crypto trader. Three fully isolated backend services plus a web front end, with no shared code. They talk to each other only through JSON over HTTP.

| Service | Path | Purpose | Spends tokens? |
|---|---|---|---|
| **Api** | `src/Coppertop.Api` | The only service that writes to SQLite: opportunities, vetoes, positions, orders, trades, token usage and the summary. | No |
| **Trader** | `src/Coppertop.Trader` | Runs a plain C# loop every 30s: reads opportunities, checks prices, enters positions and manages exits. | No |
| **Research** | `src/Coppertop.Research` | Screens the watchlist each hour (plain code), checks the survivors for bad news with Sonar, and publishes opportunities (what to buy and within which limits). | Yes, Sonar news checks (budgeted) |
| **Web** | `src/Coppertop.Web` | React dashboard: summary, live unrealized P&L, open positions, opportunities, orders, trades, closed positions, stats and token usage, plus the kill switch. Refreshes every 10s. | No |

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
- `OPENROUTER_API_KEY` turns on Research's Sonar news vetoes. Without it, screened coins are published unchecked.
- `KRAKEN_API_KEY` and `KRAKEN_API_SECRET` (the Kraken key and its private key) turn on Kraken's `validate=true` order checks and the read-only Kraken account balances on the dashboard. The key needs "Query Funds" and "Create & Modify Orders"; never give it "Withdraw Funds". The names must be exactly these; compose won't pick up other names such as `API_KEY` / `PRIVATE_KEY`.

Each service has its own `Dockerfile` in its folder and can be built on its own, e.g. `docker build src/Coppertop.Trader`. The Dockerfiles cross-compile for the target architecture (`--platform=$BUILDPLATFORM` + `dotnet publish -a $TARGETARCH`), so building arm64 images on an x64 machine is fast.

## CI / Raspberry Pi
Each service has its own workflow in `.github/workflows/` (`api.yml`, `trader.yml`, `research.yml`, `web.yml`).
- A workflow runs on a push to `main` that touches that service's folder, or when started manually.
- It builds `linux/amd64` and `linux/arm64` images and pushes them to `ghcr.io/<owner>/coppertop-<service>`, tagged `latest` and with that workflow's `github.run_number`.
- The Api, Trader and Research workflows run their unit tests first.

On the Pi:
```bash
docker compose -f compose.pi.yaml pull && docker compose -f compose.pi.yaml up -d
TRADER_TAG=42 docker compose -f compose.pi.yaml up -d trader   # pin/roll back one service
```
New GHCR packages are private by default. Either run `docker login ghcr.io` on the Pi with a personal access token that has `read:packages`, or make each package public.

## Run locally
```bash
cd src/Coppertop.Api      && dotnet run   # http://localhost:5057
cd src/Coppertop.Research && dotnet run   # screens the watchlist, publishes approved coins, then repeats hourly (retries after 1 min on failure)
cd src/Coppertop.Trader   && dotnet run   # paper-trades every 30s
cd src/Coppertop.Web      && npm install && npm run dev   # http://localhost:5173, forwards /api to :5057
dotnet test src/Coppertop.Api.Tests
dotnet test src/Coppertop.Trader.Tests
dotnet test src/Coppertop.Research.Tests
```
In dev, set `COPPERTOP_API_URL` and `COPPERTOP_API_KEY` (env vars or `src/Coppertop.Web/.env.local`) if the Api isn't on :5057 or has a key.
Quick checks: `curl localhost:5057/summary`, `curl "localhost:5057/positions?status=open"`.

## Safety
- The Trader only runs in **Paper** mode. Any other `Trader:Mode` makes it refuse to start.
- If Kraken credentials are set (`Kraken__ApiKey`, `Kraken__ApiSecret`, or user-secrets), the Trader sends each entry to Kraken with `validate=true`. Kraken checks the order and places nothing. Fills are always simulated.

## Dashboard controls (kill switch)
- **Pause entries** (`PUT /control {entriesPaused}`): the Trader stops placing buys and cancels resting ones. Take-profits and stop-losses keep running. **Resume entries** turns buying back on.
- **Flatten all** (`POST /control/flatten`): pauses entries, and on its next cycle the Trader cancels every open order and market-sells every position (taker fee plus slippage, close reason `flatten`). It then calls `POST /control/flatten/ack`. Entries stay paused until you resume them.
- **Heartbeat**: each cycle the Trader posts `POST /control/heartbeat` with its mode, starting cash and latest prices (`GET /marks`). The dashboard uses these for unrealized P&L (at the bid, before exit fees), paper equity, and the Trader "running / stale" indicator.
- **Stats** (`GET /stats?since=`): win rate, profit factor, average win and loss, best and worst trade, average hold time, P&L by exit reason, and the entry fill rate.

## Kraken account balances

When Kraken credentials are set, the Trader's `AccountWorker` reads the real account every `Kraken:AccountRefreshSeconds` (default 60, minimum 15) and posts a snapshot to the Api (`POST /account`, read with `GET /account`). It is read-only and never places orders.

- It uses the private `BalanceEx` endpoint, which returns `{"ZUSD":{"balance":"58.03","hold_trade":"0"}, "XXBT":{...}, "USD.HOLD":{...}}`. Asset codes mix legacy prefixes (`XXBT`, `XETH`, `ZUSD`), plain codes (`SOL`, `ADA`) and suffixes (`.HOLD`, `.S` staked, `.F` earn). `hold_trade` is the amount reserved by open orders, so available = balance − hold.
- Each asset is valued at the bid of its USD pair. The pair is found through the full `AssetPairs` list (cached for 24h, matching `base` and a `ZUSD`/`USD` quote), then one `Ticker` call prices them all. USD counts at $1. Assets with no USD pair show as unpriced, and zero balances are dropped.
- If Kraken returns an error (for example `EGeneral:Permission denied` when the key lacks "Query Funds"), the dashboard shows it and keeps the last good snapshot.
- **Dust** is a balance Kraken won't let you sell: below the USD pair's minimum order size (`ordermin`) or worth less than its minimum order value (`costmin`) at the bid. USD is never dust, and neither is an asset with no USD pair, because it can't be judged. Dust still counts in the total but is hidden from the **Kraken account** tab by default, with a "Show dust" toggle.
- The dashboard shows an account strip (total value, USD cash available, sync time) and a **Kraken account** tab.
- These are real balances and are separate from the paper P&L. Paper trading doesn't touch them.

## Research screener
Each cycle, Research screens every watchlist coin using Kraken public data only (tickers, 15m and 1h candles, 1.1s between calls to respect the rate limit it shares with the Trader). No tokens are spent.
- **Hard rules** (all reasons are listed): spread > `MaxSpreadPct` (0.30%), 24h volume < `MinVolume24hUsd` ($1M), 24h drop > `MaxDrop24hPct` (8%), 1h downtrend (SMA50 < SMA200, if `RejectDowntrend`).
- **Dip replay**: it replays the Trader's dip strategy over the last ~180h of 15m candles (entry below SMA20 − kσ, maker fee on TP, taker fee + slippage on SL and time stop; if a candle hits both, the stop wins).
- **Per-coin tuning (walk-forward)**: every combination of TP (`TuneTakeProfitPcts`, default 1–3%), SL (`TuneStopLossPcts`, 1–3%), band k (`TuneBandStdDevs`, 1.5/2/2.5) and time stop (`TuneMaxHoldHours`, 4/12/24h/none) is replayed on the older `TuneTrainFraction` (2/3) of the candles. TPs that net less than `MinNetProfitPct` (0.30%) per win are skipped. The best total net with at least `MinSimTrades` (3) resolved trades wins, and is then replayed on the newer 1/3 it never saw. A coin is approved only if that test part has at least `MinTestTrades` (2) resolved trades and an average net > `MinSimAvgNetPct` (0%). Most training winners lose on the test part; that's the check working.
- **Time stop**: a position held longer than the tuned hold is sold at market (bid less slippage, taker fee), with reason `time_stop`.
- **Confidence** is the 95% Wilson lower bound of the win rate of the chosen setting over the whole period. Only the top `MaxOpportunities` (5) by test average net are published, as `dip` opportunities that expire after 4h.
- **Signal passed to the Trader**: each opportunity carries its tuned TP/SL plus a signal (candle interval, SMA period, band k, max hold minutes), stored in the `opportunity_signal` table (created automatically, so existing databases on the Pi just gain the table). The Trader uses it instead of its own defaults; opportunities without a signal use the Trader's `CandleIntervalMinutes`/`SmaPeriod`/`BandStdDevs` and `MaxHoldMinutes` (0 = no time stop).
- Coins that stop passing have their active `dip` opportunity cancelled.
- All results (approved and rejected, with metrics and reasons) go to `POST /research/screen` and show on the dashboard **Research** tab (`GET /research/screen`).
- The fee and slippage settings, and the default `CandleIntervalMinutes` (15) / `SmaPeriod`, must be kept in step with the Trader's, or the replay stops describing what the Trader does.
- With TP +1.5% / SL −2%, a win nets about +0.99% and a loss about −2.73% after fees, so the untuned strategy needs about a **73% win rate** to break even. Tuning lets each coin pick its own ratio. The test part is short (~60h, often 2–5 trades), so an approval is thin evidence; the Predicted vs actual tab is where it gets confirmed or not.

## News vetoes (Sonar)
After screening, Research asks Perplexity Sonar (`perplexity/sonar` through OpenRouter, `OpenRouter:ApiKey`) about each coin that passed, and only those, so a normal cycle makes 0–5 calls.
- The question: is there a specific, recent (`NewsLookbackHours`, 48h) negative event about **this coin itself**? That means a hack of its own network or token, its delisting or a trading halt, regulatory action aimed at it, a chain halt, a big unlock or dump, a depeg, or a project collapse. Hacks at other exchanges, market moves and opinion pieces don't count, and when unsure it allows. It replies with JSON `{"verdict":"allow|block","reason":"..."}`.
- **Block**: Research posts a veto (`NewsVetoHours`, 12h), which cancels the coin's opportunities and blocks new ones. The Research tab shows "news veto: …".
- **Allow**: the opportunity is published with the news summary appended to its reason.
- **Couldn't check** (error, unparseable reply or budget used up): the coin is held back this cycle, unless `PublishWhenNewsUnavailable` is true.
- Coins already vetoed are skipped without a call. A verdict is reused for `NewsCacheHours` (6h), so the same coin is checked at most about 4 times a day.
- **Cost**: about $0.005 per call, almost all of it Perplexity's search fee. Every call is recorded through `POST /token-usage` (service `research`, purpose `news-check <ASSET>`) with the cost OpenRouter reports, and shows in the dashboard's token usage. Calls stop for the rest of the UTC day once that day's research spend reaches `NewsDailyBudgetUsd` ($0.25, about 48 calls).

## Predicted vs actual (trade context)
Every entry records the conditions it was made under, so results can later be compared with predictions. No LLM is involved.
- **When Research publishes** an opportunity, it sends a `context` with it (stored in `opportunity_context`): the screen metrics (RSI, ATR, trend, spread, volume, 24h/7d change), the replay's prediction (wins/losses, win rate, average net, hold time), the news verdict, and the replay settings.
- **When the Trader places** an entry order, it sends a `context` with it (stored in `order_context`): bid/ask/spread, SMA, σ, band, how far below the band the ask was, z-score, RSI on the signal's candle closes, 1h and 4h change, the signal used (band k, interval, max hold), hour and weekday (UTC), TP/SL, the opportunity's confidence and age, and open positions and cash.
- Both are JSON so fields can be added without schema changes. Each has a `version`.
- `GET /analysis/entries?since=` returns every entry order with both contexts and its outcome: `win`, `loss`, `holding`, `waiting`, `unfilled` or `rejected`, plus fill wait, hold time and actual net % (realized P&L after both fees ÷ cost).
- The dashboard's **Predicted vs actual** tab shows fill rate, actual against predicted win rate and net per trade, results grouped by coin, by RSI at entry and by trend, and every entry.
- Entries made before this was added have no context and group as "unknown". Groups under about 15–20 closed trades are mostly noise.

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
- Research approves coins with plain-code rules and uses Sonar only to veto on news. Next: per-coin memory, so past results and news inform the next run.
- Tuning tests on only ~60h of unseen data; a longer history (more candles, or stored candles) would make approvals more trustworthy.
- Live mode: Kraken supports only one conditional close order per entry, so the take-profit sits on Kraken and the Trader must watch the stop-loss itself.
- Unrealized P&L uses prices from the Trader's last cycle, so it only updates while the Trader is running.
- Prices are polled over REST. Switch to WebSocket if polling is too slow.
- Money columns are SQLite `REAL`, so they aren't exact to the cent. Switch to integer cents or TEXT if exactness matters.
