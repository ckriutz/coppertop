import { useCallback, useEffect, useState } from 'react'
import { loadDashboard, type Dashboard, type Opportunity, type Order, type Position, type TokenUsage, type Trade, type Veto } from './api'
import { ago, money, num, pct, signClass, time, until } from './format'
import { Table, type Column } from './Table'

const REFRESH_MS = 10_000

type Tab = 'positions' | 'opportunities' | 'orders' | 'trades' | 'history' | 'tokens'

const TABS: { id: Tab; label: string }[] = [
  { id: 'positions', label: 'Open positions' },
  { id: 'opportunities', label: 'Opportunities' },
  { id: 'orders', label: 'Orders' },
  { id: 'trades', label: 'Trades' },
  { id: 'history', label: 'Closed positions' },
  { id: 'tokens', label: 'Token usage' },
]

export default function App() {
  const [data, setData] = useState<Dashboard | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null)
  const [tab, setTab] = useState<Tab>('positions')
  const [now, setNow] = useState(() => Date.now())

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const d = await loadDashboard(signal)
      setData(d)
      setError(null)
      setUpdatedAt(new Date())
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
        <>
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
              <Table rows={data.openPositions} columns={openPositionColumns(now)} rowKey={(p) => p.id} empty="No open positions." />
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
            {tab === 'tokens' && <Table rows={data.tokenUsage} columns={tokenColumns} rowKey={(t) => t.id} empty="No LLM calls recorded yet." />}
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
    case 'tokens': return d.tokenUsage.length
  }
}

function SummaryCards({ data }: { data: Dashboard }) {
  const s = data.summary
  const winRate = s.closedPositions > 0 ? (s.winningPositions / s.closedPositions) * 100 : null
  const cards: { label: string; value: string; className?: string; hint?: string }[] = [
    { label: 'Net after tokens', value: money(s.netAfterTokensUsd), className: signClass(s.netAfterTokensUsd), hint: 'Realized P&L minus LLM cost. This is the number that has to stay positive.' },
    { label: 'Realized P&L', value: money(s.realizedPnlUsd), className: signClass(s.realizedPnlUsd), hint: 'After trading fees.' },
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

const openPositionColumns = (now: number): Column<Position>[] => [
  { header: 'Asset', cell: (p) => <strong>{p.asset}</strong> },
  { header: 'Volume', cell: (p) => num(p.volume), align: 'right' },
  { header: 'Entry', cell: (p) => num(p.entryPrice), align: 'right' },
  { header: 'Cost', cell: (p) => money(p.costUsd), align: 'right' },
  { header: 'Take profit', cell: (p) => <span className="pos">{num(p.takeProfitPrice)} ({pct((p.takeProfitPrice / p.entryPrice - 1) * 100, 2)})</span>, align: 'right' },
  { header: 'Stop loss', cell: (p) => <span className="neg">{num(p.stopLossPrice)} ({pct((p.stopLossPrice / p.entryPrice - 1) * 100, 2)})</span>, align: 'right' },
  { header: 'Opened', cell: (p) => <span title={time(p.openedAt)}>{ago(p.openedAt, now)}</span> },
  { header: 'Mode', cell: (p) => simTag(p.isSimulated) },
]

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
  const m = Math.round((new Date(to).getTime() - new Date(from).getTime()) / 60000)
  return m < 60 ? `${m}m` : `${Math.floor(m / 60)}h ${m % 60}m`
}
