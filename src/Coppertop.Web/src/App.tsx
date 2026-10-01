import { useCallback, useEffect, useState } from 'react'
import {
  loadDashboard, requestFlatten, setEntriesPaused,
  type Account, type AccountBalance, type EntryAnalysis, type ScreenResult, type Control, type Dashboard, type Mark, type Opportunity, type Order, type Position, type Stats, type TokenUsage, type Trade, type Veto,
} from './api'
import { ago, money, num, pct, signClass, time, until } from './format'
import { Table, type Column } from './Table'

const REFRESH_MS = 10_000

type Tab = 'positions' | 'research' | 'opportunities' | 'orders' | 'trades' | 'history' | 'stats' | 'learning' | 'tokens' | 'account'

const TABS: { id: Tab; label: string }[] = [
  { id: 'positions', label: 'Open positions' },
  { id: 'research', label: 'Research' },
  { id: 'opportunities', label: 'Opportunities' },
  { id: 'orders', label: 'Orders' },
  { id: 'trades', label: 'Trades' },
  { id: 'history', label: 'Closed positions' },
  { id: 'stats', label: 'Stats' },
  { id: 'learning', label: 'Predicted vs actual' },
  { id: 'tokens', label: 'Token usage' },
  { id: 'account', label: 'Kraken account' },
]

export default function App() {
  const [data, setData] = useState<Dashboard | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null)
  const [tab, setTab] = useState<Tab>('positions')
  const [now, setNow] = useState(() => Date.now())
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const d = await loadDashboard(signal)
      setData(d)
      setError(null)
      setUpdatedAt(new Date())
      setNow(Date.now())
    } catch (e) {
      if (signal?.aborted) return
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void refresh(controller.signal)
    const id = setInterval(() => {
      void refresh(controller.signal)
      setNow(Date.now())
    }, REFRESH_MS)
    return () => {
      controller.abort()
      clearInterval(id)
    }
  }, [refresh])

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true)
    setActionError(null)
    try {
      await action()
      await refresh()
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const flatten = () => {
    if (!window.confirm('Pause entries, cancel all resting orders and market-sell every open position on the next Trader cycle?')) return
    void act(requestFlatten)
  }

  const closed = data?.positions.filter((p) => p.status === 'closed') ?? []
  const simulated = data ? data.openPositions.some((p) => p.isSimulated) || data.orders.some((o) => o.isSimulated) : false

  return (
    <div className="app">
      <header>
        <h1>
          Coppertop {simulated && <span className="badge paper">PAPER</span>}
        </h1>
        <div className="status">
          {error ? (
            <span className="neg">● API unreachable: {error}</span>
          ) : updatedAt ? (
            <span className="pos">● Live · updated {updatedAt.toLocaleTimeString()}</span>
          ) : (
            <span>Loading…</span>
          )}
          <button onClick={() => void refresh()}>Refresh</button>
        </div>
      </header>

      {data && (
        <ControlBar
          control={data.control}
          now={now}
          busy={busy}
          error={actionError}
          onTogglePause={() => void act(() => setEntriesPaused(!data.control.entriesPaused))}
          onFlatten={flatten}
        />
      )}

      {data && (
        <>
          <AccountCards account={data.account} now={now} onOpen={() => setTab('account')} />
          <SummaryCards data={data} />

          <nav className="tabs">
            {TABS.map((t) => (
              <button key={t.id} className={t.id === tab ? 'active' : undefined} onClick={() => setTab(t.id)}>
                {t.label}
                <span className="count">{countFor(t.id, data, closed)}</span>
              </button>
            ))}
          </nav>

          <section className="panel">
            {tab === 'positions' && (
              <Table rows={data.openPositions} columns={openPositionColumns(now, data.marks)} rowKey={(p) => p.id} empty="No open positions." />
            )}
            {tab === 'research' && <ResearchPanel rows={data.screen} now={now} />}
            {tab === 'opportunities' && (
              <>
                <Table rows={data.opportunities} columns={opportunityColumns(now)} rowKey={(o) => o.id} empty="No active opportunities. Research hasn't approved anything right now." />
                {data.vetoes.length > 0 && (
                  <>
                    <h3>Active vetoes</h3>
                    <Table rows={data.vetoes} columns={vetoColumns(now)} rowKey={(v) => v.id} empty="" />
                  </>
                )}
              </>
            )}
            {tab === 'orders' && <Table rows={data.orders} columns={orderColumns} rowKey={(o) => o.id} empty="No orders yet." />}
            {tab === 'trades' && <Table rows={data.trades} columns={tradeColumns} rowKey={(t) => t.id} empty="No trades yet." />}
            {tab === 'history' && <Table rows={closed} columns={closedColumns} rowKey={(p) => p.id} empty="No closed positions yet." />}
            {tab === 'stats' && <StatsPanel stats={data.stats} />}
            {tab === 'learning' && <LearningPanel rows={data.entries} />}
            {tab === 'tokens' && <Table rows={data.tokenUsage} columns={tokenColumns} rowKey={(t) => t.id} empty="No LLM calls recorded yet." />}
            {tab === 'account' && <AccountPanel account={data.account} now={now} />}
          </section>
        </>
      )}
    </div>
  )
}

function countFor(tab: Tab, d: Dashboard, closed: Position[]): number {
  switch (tab) {
    case 'positions': return d.openPositions.length
    case 'research': return d.screen.filter((r) => r.approved).length
    case 'opportunities': return d.opportunities.length
    case 'orders': return d.orders.length
    case 'trades': return d.trades.length
    case 'history': return closed.length
    case 'stats': return d.stats.closedPositions
    case 'learning': return d.entries.length
    case 'tokens': return d.tokenUsage.length
    case 'account': return d.account.balances.filter((b) => !b.isDust).length
  }
}

function AccountCards({ account: a, now, onOpen }: { account: Account; now: number; onOpen: () => void }) {
  if (!a.connected && !a.error) return null
  const holdings = a.balances.filter((b) => !/^Z?USD(\.|$)/.test(b.asset) && !b.isDust)
  const status = a.error
    ? { text: `Sync error ${a.errorAt ? ago(a.errorAt, now) : ''}: ${a.error}`, className: 'neg' }
    : { text: `Synced ${a.syncedAt ? ago(a.syncedAt, now) : '—'}`, className: 'muted' }
  return (
    <div className="account-strip" onClick={onOpen} title="Open the Kraken account tab">
      <span className="account-title">Kraken account <span className="badge live small">real</span></span>
      <span>Total <strong>{a.connected ? money(a.totalUsd) : '—'}</strong>{a.unpricedAssets > 0 && <span className="muted"> +{a.unpricedAssets} unpriced</span>}</span>
      <span>USD cash <strong>{money(a.cashAvailableUsd)}</strong>{a.cashUsd !== a.cashAvailableUsd && <span className="muted"> of {money(a.cashUsd)}</span>}</span>
      <span>Holdings <strong>{holdings.length}</strong></span>
      <span className={status.className}>{status.text}</span>
    </div>
  )
}

function AccountPanel({ account: a, now }: { account: Account; now: number }) {
  const [showDust, setShowDust] = useState(false)
  const rows = showDust ? a.balances : a.balances.filter((b) => !b.isDust)
  if (!a.connected && !a.error) {
    return (
      <p className="empty">
        Not connected. Give the Trader a read-only Kraken key (KRAKEN_API_KEY / KRAKEN_API_SECRET with "Query Funds") to see real balances here.
      </p>
    )
  }
  return (
    <>
      {a.error && <p className="neg">Last sync failed {a.errorAt ? ago(a.errorAt, now) : ''}: {a.error}. Showing the last good snapshot.</p>}
      <p className="muted">
        Real balances from Kraken, valued at the USD bid. Synced {a.syncedAt ? ago(a.syncedAt, now) : 'never'}.
        {a.dustAssets > 0 && (
          <>
            {' '}{a.dustAssets} dust balance(s) worth {money(a.dustUsd)} {showDust ? 'shown muted' : 'hidden'}: too small to sell on Kraken.{' '}
            <button className="link" onClick={() => setShowDust(!showDust)}>{showDust ? 'Hide dust' : 'Show dust'}</button>
          </>
        )}
      </p>
      <Table rows={rows} columns={accountColumns} rowKey={(b) => b.asset} empty="Nothing but dust." />
    </>
  )
}

const dim = (b: AccountBalance) => (b.isDust ? 'muted' : '')

const accountColumns: Column<AccountBalance>[] = [
  { header: 'Asset', cell: (b) => <strong title={b.asset}>{b.displayName}</strong>, className: dim },
  { header: 'Balance', cell: (b) => num(b.balance, 10), align: 'right', className: dim },
  { header: 'On hold', cell: (b) => (b.hold ? num(b.hold) : '—'), align: 'right', className: dim },
  { header: 'Available', cell: (b) => num(b.available, 10), align: 'right', className: dim },
  { header: 'Price (USD)', cell: (b) => (b.priceUsd == null ? <span className="muted">no USD pair</span> : num(b.priceUsd, 6)), align: 'right', className: dim },
  { header: 'Value', cell: (b) => money(b.valueUsd), align: 'right', className: dim },
]

function ControlBar(props: {
  control: Control
  now: number
  busy: boolean
  error: string | null
  onTogglePause: () => void
  onFlatten: () => void
}) {
  const { control: c, now, busy, error } = props
  const trader = traderHealth(c, now)
  return (
    <div className="controlbar">
      <span className={trader.className} title={c.lastSeenAt ? time(c.lastSeenAt) : undefined}>● Trader {trader.label}</span>
      {c.entriesPaused ? <span className="badge warn">ENTRIES PAUSED</span> : <span className="badge ok">ENTRIES ON</span>}
      {c.flattenRequested && <span className="badge live">FLATTEN PENDING</span>}
      <span className="spacer" />
      {error && <span className="neg">{error}</span>}
      <button disabled={busy} onClick={props.onTogglePause}>{c.entriesPaused ? 'Resume entries' : 'Pause entries'}</button>
      <button className="danger" disabled={busy || c.flattenRequested} onClick={props.onFlatten}>Flatten all</button>
    </div>
  )
}

function traderHealth(c: Control, now: number): { label: string; className: string } {
  if (!c.lastSeenAt) return { label: 'never seen', className: 'neg' }
  const ageSec = (now - new Date(c.lastSeenAt).getTime()) / 1000
  // Allow a few missed cycles (Kraken hiccups) before calling it stale.
  const staleAfter = Math.max(3 * (c.cycleSeconds ?? 30), 90)
  const mode = c.mode ? ` · ${c.mode.toLowerCase()}` : ''
  return ageSec > staleAfter
    ? { label: `stale · last seen ${ago(c.lastSeenAt, now)}`, className: 'neg' }
    : { label: `running${mode} · seen ${ago(c.lastSeenAt, now)}`, className: 'pos' }
}

// Mark-to-bid value minus cost (entry fee included). The exit fee isn't deducted yet.
function unrealized(p: Position, marks: Record<string, Mark>): number | null {
  const m = marks[p.asset]
  return m ? m.bid * p.volume - p.costUsd : null
}

function SummaryCards({ data }: { data: Dashboard }) {
  const s = data.summary
  const winRate = s.closedPositions > 0 ? (s.winningPositions / s.closedPositions) * 100 : null
  const values = data.openPositions.map((p) => unrealized(p, data.marks))
  const priced = values.filter((v): v is number => v != null)
  const unrealizedTotal = priced.reduce((a, b) => a + b, 0)
  const start = data.control.paperStartingCashUsd
  const cards: { label: string; value: string; className?: string; hint?: string }[] = [
    { label: 'Net after tokens', value: money(s.netAfterTokensUsd), className: signClass(s.netAfterTokensUsd), hint: 'Realized P&L minus LLM cost. This is the number that has to stay positive.' },
    { label: 'Realized P&L', value: money(s.realizedPnlUsd), className: signClass(s.realizedPnlUsd), hint: 'After trading fees.' },
    {
      label: 'Unrealized P&L',
      value: priced.length ? money(unrealizedTotal) : '—',
      className: signClass(unrealizedTotal),
      hint: `At the bid, before exit fees${priced.length < values.length ? ` · ${values.length - priced.length} unpriced` : ''}`,
    },
    ...(start != null
      ? [{ label: 'Paper equity', value: money(start + s.realizedPnlUsd + unrealizedTotal), hint: `Started with ${money(start)}` }]
      : []),
    { label: 'Token cost', value: money(s.tokenCostUsd) },
    { label: 'Fees paid', value: money(s.feesUsd) },
    { label: 'Open exposure', value: money(s.openExposureUsd), hint: `${s.openPositions} open position(s)` },
    { label: 'Win rate', value: winRate == null ? '—' : pct(winRate, 0), hint: `${s.winningPositions} of ${s.closedPositions} closed` },
    { label: 'Active opportunities', value: String(s.activeOpportunities) },
  ]
  return (
    <div className="cards">
      {cards.map((c) => (
        <div className="card" key={c.label} title={c.hint}>
          <div className="label">{c.label}</div>
          <div className={`value ${c.className ?? ''}`}>{c.value}</div>
          {c.hint && <div className="hint">{c.hint}</div>}
        </div>
      ))}
    </div>
  )
}

const simTag = (sim: boolean) => (sim ? <span className="badge paper small">paper</span> : <span className="badge live small">live</span>)

const openPositionColumns = (now: number, marks: Record<string, Mark>): Column<Position>[] => [
  { header: 'Asset', cell: (p) => <strong>{p.asset}</strong> },
  { header: 'Volume', cell: (p) => num(p.volume), align: 'right' },
  { header: 'Entry', cell: (p) => num(p.entryPrice), align: 'right' },
  {
    header: 'Bid',
    cell: (p) => {
      const m = marks[p.asset]
      return m ? <span title={`updated ${ago(m.updatedAt, now)}`}>{num(m.bid)}</span> : '—'
    },
    align: 'right',
  },
  { header: 'Cost', cell: (p) => money(p.costUsd), align: 'right' },
  {
    header: 'Unrealized',
    cell: (p) => {
      const u = unrealized(p, marks)
      return u == null ? '—' : `${money(u)} (${pct((u / p.costUsd) * 100, 2)})`
    },
    align: 'right',
    className: (p) => signClass(unrealized(p, marks)),
  },
  { header: 'SL → TP', cell: (p) => <Progress position={p} mark={marks[p.asset]} /> },
  { header: 'Take profit', cell: (p) => <span className="pos">{num(p.takeProfitPrice)} ({pct((p.takeProfitPrice / p.entryPrice - 1) * 100, 2)})</span>, align: 'right' },
  { header: 'Stop loss', cell: (p) => <span className="neg">{num(p.stopLossPrice)} ({pct((p.stopLossPrice / p.entryPrice - 1) * 100, 2)})</span>, align: 'right' },
  { header: 'Opened', cell: (p) => <span title={time(p.openedAt)}>{ago(p.openedAt, now)}</span> },
  { header: 'Mode', cell: (p) => simTag(p.isSimulated) },
]

function Progress({ position: p, mark }: { position: Position; mark?: Mark }) {
  if (!mark) return <span className="muted">—</span>
  const f = Math.min(1, Math.max(0, (mark.bid - p.stopLossPrice) / (p.takeProfitPrice - p.stopLossPrice)))
  const entry = (p.entryPrice - p.stopLossPrice) / (p.takeProfitPrice - p.stopLossPrice)
  return (
    <div className="progress" title={`Bid is ${pct(f * 100, 0)} of the way from stop loss to take profit`}>
      <div className="entry" style={{ left: `${entry * 100}%` }} />
      <div className={`dot ${mark.bid >= p.entryPrice ? 'up' : 'down'}`} style={{ left: `${f * 100}%` }} />
    </div>
  )
}

function StatsPanel({ stats: s }: { stats: Stats }) {
  const items: { label: string; value: string; className?: string; hint?: string }[] = [
    { label: 'Closed trades', value: String(s.closedPositions), hint: `${s.wins} won · ${s.losses} lost` },
    { label: 'Win rate', value: pct(s.winRatePct, 0) },
    { label: 'Profit factor', value: s.profitFactor == null ? '—' : s.profitFactor.toFixed(2), hint: 'Gross profit ÷ gross loss. Above 1 means winners outweigh losers.' },
    { label: 'Avg per trade', value: money(s.avgPnlUsd), className: signClass(s.avgPnlUsd) },
    { label: 'Avg win / loss', value: `${money(s.avgWinUsd)} / ${money(s.avgLossUsd)}` },
    { label: 'Best / worst', value: `${money(s.bestUsd)} / ${money(s.worstUsd)}` },
    { label: 'Avg hold', value: s.avgHoldMinutes == null ? '—' : minutes(s.avgHoldMinutes) },
    {
      label: 'Entry fill rate',
      value: pct(s.entryOrders.fillRatePct, 0),
      hint: `${s.entryOrders.filled} filled · ${s.entryOrders.cancelled} cancelled · ${s.entryOrders.open} waiting`,
    },
  ]
  return (
    <>
      <div className="cards">
        {items.map((c) => (
          <div className="card" key={c.label} title={c.hint}>
            <div className="label">{c.label}</div>
            <div className={`value ${c.className ?? ''}`}>{c.value}</div>
            {c.hint && <div className="hint">{c.hint}</div>}
          </div>
        ))}
      </div>
      <h3>By exit reason</h3>
      <Table
        rows={s.byReason}
        rowKey={(r) => r.reason}
        empty="No closed positions yet."
        columns={[
          { header: 'Reason', cell: (r) => r.reason.replace('_', ' ') },
          { header: 'Count', cell: (r) => r.count, align: 'right' },
          { header: 'P&L', cell: (r) => money(r.pnlUsd), align: 'right', className: (r) => signClass(r.pnlUsd) },
        ]}
      />
    </>
  )
}

const opportunityColumns = (now: number): Column<Opportunity>[] => [
  { header: 'Asset', cell: (o) => <strong>{o.asset}</strong> },
  { header: 'Strategy', cell: (o) => o.strategy },
  { header: 'Max entry', cell: (o) => num(o.maxEntryPrice), align: 'right' },
  { header: 'TP / SL', cell: (o) => `+${pct(o.takeProfitPct)} / −${pct(o.stopLossPct)}`, align: 'right' },
  { header: 'Max spend', cell: (o) => money(o.maxSpendUsd), align: 'right' },
  { header: 'Confidence', cell: (o) => pct(o.confidence * 100, 0), align: 'right' },
  { header: 'Expires in', cell: (o) => <span title={time(o.expiresAt)}>{until(o.expiresAt, now)}</span> },
  { header: 'Reason', cell: (o) => <span className="reason">{o.reason}</span> },
]

const vetoColumns = (now: number): Column<Veto>[] => [
  { header: 'Asset', cell: (v) => <strong>{v.asset}</strong> },
  { header: 'Reason', cell: (v) => v.reason },
  { header: 'Expires in', cell: (v) => until(v.expiresAt, now) },
]

const orderColumns: Column<Order>[] = [
  { header: 'Time', cell: (o) => time(o.createdAt) },
  { header: 'Asset', cell: (o) => <strong>{o.asset}</strong> },
  { header: 'Side', cell: (o) => <span className={o.side === 'buy' ? 'pos' : 'neg'}>{o.side}</span> },
  { header: 'Type', cell: (o) => o.orderType },
  { header: 'Purpose', cell: (o) => o.purpose.replace('_', ' ') },
  { header: 'Price', cell: (o) => num(o.price), align: 'right' },
  { header: 'Volume', cell: (o) => num(o.volume), align: 'right' },
  { header: 'Status', cell: (o) => <span className={`status-${o.status}`}>{o.status}</span> },
  { header: 'Mode', cell: (o) => simTag(o.isSimulated) },
  { header: 'Note', cell: (o) => <span className="reason">{o.note ?? ''}</span> },
]

const tradeColumns: Column<Trade>[] = [
  { header: 'Time', cell: (t) => time(t.executedAt) },
  { header: 'Asset', cell: (t) => <strong>{t.asset}</strong> },
  { header: 'Side', cell: (t) => <span className={t.side === 'buy' ? 'pos' : 'neg'}>{t.side}</span> },
  { header: 'Price', cell: (t) => num(t.price), align: 'right' },
  { header: 'Volume', cell: (t) => num(t.volume), align: 'right' },
  { header: 'Value', cell: (t) => money(t.price * t.volume), align: 'right' },
  { header: 'Fee', cell: (t) => money(t.feeUsd), align: 'right' },
  { header: 'Mode', cell: (t) => simTag(t.isSimulated) },
]

const closedColumns: Column<Position>[] = [
  { header: 'Closed', cell: (p) => time(p.closedAt) },
  { header: 'Asset', cell: (p) => <strong>{p.asset}</strong> },
  { header: 'Entry', cell: (p) => num(p.entryPrice), align: 'right' },
  { header: 'Exit', cell: (p) => num(p.exitPrice), align: 'right' },
  { header: 'Reason', cell: (p) => (p.closeReason ?? '').replace('_', ' ') },
  { header: 'Fees', cell: (p) => money(p.entryFeeUsd + (p.exitFeeUsd ?? 0)), align: 'right' },
  { header: 'P&L', cell: (p) => money(p.realizedPnlUsd), align: 'right', className: (p) => signClass(p.realizedPnlUsd) },
  { header: 'Held', cell: (p) => (p.closedAt ? held(p.openedAt, p.closedAt) : '—') },
  { header: 'Mode', cell: (p) => simTag(p.isSimulated) },
]

const tokenColumns: Column<TokenUsage>[] = [
  { header: 'Time', cell: (t) => time(t.createdAt) },
  { header: 'Service', cell: (t) => t.service },
  { header: 'Model', cell: (t) => t.model },
  { header: 'Purpose', cell: (t) => t.purpose },
  { header: 'In', cell: (t) => t.inputTokens.toLocaleString(), align: 'right' },
  { header: 'Out', cell: (t) => t.outputTokens.toLocaleString(), align: 'right' },
  { header: 'Cost', cell: (t) => money(t.costUsd), align: 'right' },
]

function held(from: string, to: string): string {
  return minutes((new Date(to).getTime() - new Date(from).getTime()) / 60000)
}

function minutes(total: number): string {
  const m = Math.round(total)
  return m < 60 ? `${m}m` : `${Math.floor(m / 60)}h ${m % 60}m`
}

function ResearchPanel({ rows, now }: { rows: ScreenResult[]; now: number }) {
  if (rows.length === 0) return <p className="empty">Research hasn't screened anything yet.</p>
  const at = rows[0].screenedAt
  const hours = rows.find((r) => r.metrics?.sim)?.metrics?.sim?.hours
  return (
    <>
      <p className="muted">
        Screened {ago(at, now)}. Each asset is checked for spread, volume, crash and trend, then the Trader's dip strategy is
        replayed on the last {hours ? `${hours.toFixed(0)}h` : ''} of 5-minute candles after fees. Only assets whose replay made money
        are approved. Confidence is the win rate we can be 95% sure of.
      </p>
      <Table rows={rows} columns={screenColumns} rowKey={(r) => r.asset} empty="" />
    </>
  )
}

const pctCell = (v: number | null | undefined, digits = 1) =>
  v == null ? '—' : <span className={signClass(v)}>{v > 0 ? '+' : ''}{v.toFixed(digits)}%</span>

const screenColumns: Column<ScreenResult>[] = [
  { header: 'Verdict', cell: (r) => (r.approved ? <span className="badge ok small">approved</span> : <span className="badge no small">rejected</span>) },
  { header: 'Asset', cell: (r) => <strong>{r.asset}</strong> },
  { header: 'Confidence', cell: (r) => pct(r.confidence * 100, 0), align: 'right' },
  {
    header: 'Replay W/L',
    cell: (r) => {
      const s = r.metrics?.sim
      if (!s) return '—'
      return (
        <span title={`${s.trades} trades (${s.open} still open) over ${s.hours.toFixed(0)}h; break-even needs ${s.breakevenWinRatePct.toFixed(0)}% wins`}>
          {s.wins}/{s.losses} {s.winRatePct == null ? '' : <span className="muted">({s.winRatePct.toFixed(0)}% vs {s.breakevenWinRatePct.toFixed(0)}%)</span>}
        </span>
      )
    },
    align: 'right',
  },
  { header: 'Avg net', cell: (r) => (r.metrics?.sim && r.metrics.sim.wins + r.metrics.sim.losses > 0 ? pctCell(r.metrics.sim.avgNetPct, 2) : '—'), align: 'right' },
  { header: 'Avg hold', cell: (r) => (r.metrics?.sim?.avgHoldMinutes ? minutes(r.metrics.sim.avgHoldMinutes) : '—'), align: 'right' },
  { header: 'Spread', cell: (r) => (r.metrics ? pct(r.metrics.spreadPct, 3) : '—'), align: 'right' },
  { header: '24h vol', cell: (r) => (r.metrics ? compactUsd(r.metrics.volume24hUsd) : '—'), align: 'right' },
  { header: '24h', cell: (r) => pctCell(r.metrics?.change24hPct), align: 'right' },
  { header: '7d', cell: (r) => pctCell(r.metrics?.change7dPct), align: 'right' },
  { header: 'RSI', cell: (r) => (r.metrics?.rsi14 == null ? '—' : r.metrics.rsi14.toFixed(0)), align: 'right' },
  { header: 'Trend', cell: (r) => <span className={r.metrics?.trend === 'down' ? 'neg' : r.metrics?.trend === 'up' ? 'pos' : ''}>{r.metrics?.trend ?? '—'}</span> },
  { header: 'Why', cell: (r) => <span className="reason" title={r.reason}>{r.approved ? r.reason : r.reason.split(' | ')[0]}</span> },
]

const compactUsd = (v: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', notation: 'compact', maximumFractionDigits: 1 }).format(v)

const isClosed = (e: EntryAnalysis) => e.outcome === 'win' || e.outcome === 'loss'
const isFilled = (e: EntryAnalysis) => e.positionId != null
const predictedNet = (e: EntryAnalysis) => e.opportunityContext?.screen?.sim?.avgNetPct ?? null
const predictedWin = (e: EntryAnalysis) => e.opportunityContext?.screen?.sim?.winRatePct ?? null

function avg(values: (number | null | undefined)[]): number | null {
  const v = values.filter((x): x is number => x != null)
  return v.length === 0 ? null : v.reduce((a, b) => a + b, 0) / v.length
}

function rsiBucket(rsi: number | null | undefined): string {
  if (rsi == null) return 'unknown'
  if (rsi < 30) return '< 30 (oversold)'
  if (rsi < 45) return '30–45'
  if (rsi < 55) return '45–55'
  if (rsi < 70) return '55–70'
  return '≥ 70 (overbought)'
}

interface Group {
  key: string
  entries: number
  filled: number
  wins: number
  losses: number
  pnlUsd: number
  actualNetPct: number | null
  predictedNetPct: number | null
  predictedWinPct: number | null
}

function groupBy(rows: EntryAnalysis[], keyOf: (e: EntryAnalysis) => string): Group[] {
  const map = new Map<string, EntryAnalysis[]>()
  for (const e of rows) map.set(keyOf(e), [...(map.get(keyOf(e)) ?? []), e])
  return [...map.entries()]
    .map(([key, es]) => {
      const closed = es.filter(isClosed)
      return {
        key,
        entries: es.length,
        filled: es.filter(isFilled).length,
        wins: closed.filter((e) => e.outcome === 'win').length,
        losses: closed.filter((e) => e.outcome === 'loss').length,
        pnlUsd: closed.reduce((a, e) => a + (e.realizedPnlUsd ?? 0), 0),
        actualNetPct: avg(closed.map((e) => e.actualNetPct)),
        predictedNetPct: avg(closed.map(predictedNet)),
        predictedWinPct: avg(closed.map(predictedWin)),
      }
    })
    .sort((a, b) => b.entries - a.entries || a.key.localeCompare(b.key))
}

const groupColumns = (label: string): Column<Group>[] => [
  { header: label, cell: (g) => <strong>{g.key}</strong> },
  { header: 'Entries', cell: (g) => g.entries, align: 'right' },
  { header: 'Filled', cell: (g) => `${g.filled} (${pct((g.filled / g.entries) * 100, 0)})`, align: 'right' },
  {
    header: 'W/L',
    cell: (g) => {
      const n = g.wins + g.losses
      if (n === 0) return '—'
      return <span title={g.predictedWinPct == null ? undefined : `Replay predicted ${g.predictedWinPct.toFixed(0)}% wins`}>{g.wins}/{g.losses} <span className="muted">({((g.wins / n) * 100).toFixed(0)}%{g.predictedWinPct == null ? '' : ` vs ${g.predictedWinPct.toFixed(0)}%`})</span></span>
    },
    align: 'right',
  },
  { header: 'Predicted net', cell: (g) => pctCell(g.predictedNetPct, 2), align: 'right' },
  { header: 'Actual net', cell: (g) => pctCell(g.actualNetPct, 2), align: 'right' },
  { header: 'P&L', cell: (g) => (g.wins + g.losses ? money(g.pnlUsd) : '—'), align: 'right', className: (g) => signClass(g.pnlUsd) },
]

function LearningPanel({ rows }: { rows: EntryAnalysis[] }) {
  if (rows.length === 0)
    return <p className="empty">No entry orders yet. Each buy the Trader places will show here with the conditions it was placed under and how it turned out.</p>
  const closed = rows.filter(isClosed)
  const wins = closed.filter((e) => e.outcome === 'win').length
  const filled = rows.filter(isFilled).length
  const settled = rows.filter((e) => e.outcome !== 'waiting' && e.outcome !== 'rejected').length
  const actual = avg(closed.map((e) => e.actualNetPct))
  const predicted = avg(closed.map(predictedNet))
  const predictedWinPct = avg(closed.map(predictedWin))
  const cards = [
    { label: 'Entry orders', value: String(rows.length), hint: `${rows.filter((e) => e.outcome === 'waiting').length} waiting · ${rows.filter((e) => e.outcome === 'unfilled').length} never filled` },
    { label: 'Fill rate', value: settled ? pct((filled / settled) * 100, 0) : '—', hint: 'Filled ÷ orders that have filled or been cancelled' },
    { label: 'Win rate', value: closed.length ? pct((wins / closed.length) * 100, 0) : '—', hint: predictedWinPct == null ? `${wins} won · ${closed.length - wins} lost` : `Replay predicted ${predictedWinPct.toFixed(0)}% · ${wins} won · ${closed.length - wins} lost` },
    { label: 'Predicted net / trade', value: predicted == null ? '—' : `${predicted > 0 ? '+' : ''}${predicted.toFixed(2)}%`, className: signClass(predicted), hint: "The replay's average net for these coins when they were published" },
    { label: 'Actual net / trade', value: actual == null ? '—' : `${actual > 0 ? '+' : ''}${actual.toFixed(2)}%`, className: signClass(actual), hint: 'Realized P&L after both fees ÷ cost, closed trades only' },
  ]
  return (
    <>
      <p className="muted">
        Every buy records what Research saw when it published the coin (replay prediction, RSI, trend, news) and what the Trader saw
        when it placed the order (distance below the band, RSI on 5m candles, spread). Small groups are mostly noise. Look for patterns
        that hold over 15–20 or more closed trades.
      </p>
      <div className="cards">
        {cards.map((c) => (
          <div className="card" key={c.label} title={c.hint}>
            <div className="label">{c.label}</div>
            <div className={`value ${c.className ?? ''}`}>{c.value}</div>
            <div className="hint">{c.hint}</div>
          </div>
        ))}
      </div>
      <h3>By coin</h3>
      <Table rows={groupBy(rows, (e) => e.asset)} columns={groupColumns('Coin')} rowKey={(g) => g.key} empty="" />
      <h3>By RSI at entry (5m)</h3>
      <Table rows={groupBy(rows, (e) => rsiBucket(e.orderContext?.rsi14))} columns={groupColumns('RSI')} rowKey={(g) => g.key} empty="" />
      <h3>By 1h trend when published</h3>
      <Table rows={groupBy(rows, (e) => e.opportunityContext?.screen?.trend ?? 'unknown')} columns={groupColumns('Trend')} rowKey={(g) => g.key} empty="" />
      <h3>Entries</h3>
      <Table rows={rows} columns={entryColumns} rowKey={(e) => e.orderId} empty="" />
    </>
  )
}

const outcomeClass: Record<EntryAnalysis['outcome'], string> = {
  win: 'badge ok small', loss: 'badge no small', holding: 'badge warn small', waiting: 'badge small', unfilled: 'badge small', rejected: 'badge no small',
}

const entryColumns: Column<EntryAnalysis>[] = [
  { header: 'Placed', cell: (e) => time(e.placedAt) },
  { header: 'Asset', cell: (e) => <strong>{e.asset}</strong> },
  { header: 'Outcome', cell: (e) => <span className={outcomeClass[e.outcome]} title={e.closeReason?.replace('_', ' ') ?? e.orderStatus}>{e.outcome}</span> },
  { header: 'Predicted', cell: (e) => <span title={predictedWin(e) == null ? undefined : `Replay won ${predictedWin(e)!.toFixed(0)}% of trades`}>{pctCell(predictedNet(e), 2)}</span>, align: 'right' },
  { header: 'Actual', cell: (e) => pctCell(e.actualNetPct, 2), align: 'right' },
  { header: 'P&L', cell: (e) => money(e.realizedPnlUsd), align: 'right', className: (e) => signClass(e.realizedPnlUsd) },
  { header: 'RSI', cell: (e) => (e.orderContext?.rsi14 == null ? '—' : e.orderContext.rsi14.toFixed(0)), align: 'right' },
  { header: 'σ from SMA', cell: (e) => (e.orderContext?.zScore == null ? '—' : e.orderContext.zScore.toFixed(2)), align: 'right' },
  { header: 'Spread', cell: (e) => (e.orderContext ? pct(e.orderContext.spreadPct, 3) : '—'), align: 'right' },
  { header: '1h', cell: (e) => pctCell(e.orderContext?.change1hPct), align: 'right' },
  { header: 'Trend', cell: (e) => e.opportunityContext?.screen?.trend ?? '—' },
  { header: 'Waited', cell: (e) => (e.fillWaitMinutes == null ? '—' : minutes(e.fillWaitMinutes)), align: 'right' },
  { header: 'Held', cell: (e) => (e.holdMinutes == null ? '—' : minutes(e.holdMinutes)), align: 'right' },
  { header: 'News', cell: (e) => <span className="reason" title={e.opportunityContext?.news?.reason}>{e.opportunityContext?.news?.status ?? '—'}</span> },
]
