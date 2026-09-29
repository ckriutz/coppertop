import { useCallback, useEffect, useState } from 'react'
import {
  loadDashboard, requestFlatten, setEntriesPaused,
  type Account, type AccountBalance, type Control, type Dashboard, type Mark, type Opportunity, type Order, type Position, type Stats, type TokenUsage, type Trade, type Veto,
} from './api'
import { ago, money, num, pct, signClass, time, until } from './format'
import { Table, type Column } from './Table'

const REFRESH_MS = 10_000

type Tab = 'positions' | 'opportunities' | 'orders' | 'trades' | 'history' | 'stats' | 'tokens' | 'account'

const TABS: { id: Tab; label: string }[] = [
  { id: 'positions', label: 'Open positions' },
  { id: 'opportunities', label: 'Opportunities' },
  { id: 'orders', label: 'Orders' },
  { id: 'trades', label: 'Trades' },
  { id: 'history', label: 'Closed positions' },
  { id: 'stats', label: 'Stats' },
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
    case 'opportunities': return d.opportunities.length
    case 'orders': return d.orders.length
    case 'trades': return d.trades.length
    case 'history': return closed.length
    case 'stats': return d.stats.closedPositions
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
