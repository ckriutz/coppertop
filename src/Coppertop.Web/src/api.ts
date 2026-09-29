// The front end's own view of the Coppertop API contract (not shared with other services).

export interface Summary {
  since: string
  closedPositions: number
  winningPositions: number
  realizedPnlUsd: number
  feesUsd: number
  tokenCostUsd: number
  netAfterTokensUsd: number
  openPositions: number
  openExposureUsd: number
  activeOpportunities: number
}

export interface Position {
  id: number
  opportunityId: number | null
  asset: string
  volume: number
  entryPrice: number
  entryFeeUsd: number
  costUsd: number
  takeProfitPrice: number
  stopLossPrice: number
  status: string
  isSimulated: boolean
  openedAt: string
  closedAt: string | null
  exitPrice: number | null
  exitFeeUsd: number | null
  realizedPnlUsd: number | null
  closeReason: string | null
}

export interface Order {
  id: number
  positionId: number | null
  asset: string
  side: string
  orderType: string
  purpose: string
  price: number
  volume: number
  status: string
  krakenTxId: string | null
  isSimulated: boolean
  note: string | null
  createdAt: string
  updatedAt: string
}

export interface Trade {
  id: number
  orderId: number | null
  positionId: number | null
  asset: string
  side: string
  price: number
  volume: number
  feeUsd: number
  isSimulated: boolean
  executedAt: string
}

export interface Opportunity {
  id: number
  asset: string
  strategy: string
  maxEntryPrice: number
  takeProfitPct: number
  stopLossPct: number
  maxSpendUsd: number
  confidence: number
  reason: string
  status: string
  createdAt: string
  expiresAt: string
}

export interface Veto {
  id: number
  asset: string
  reason: string
  createdAt: string
  expiresAt: string
}

export interface TokenUsage {
  id: number
  service: string
  model: string
  purpose: string
  inputTokens: number
  outputTokens: number
  costUsd: number
  createdAt: string
}

export interface Mark {
  asset: string
  bid: number
  ask: number
  last: number
  updatedAt: string
}

export interface Control {
  entriesPaused: boolean
  flattenRequested: boolean
  updatedAt: string
  lastSeenAt: string | null
  mode: string | null
  paperStartingCashUsd: number | null
  cycleSeconds: number | null
}

export interface Stats {
  since: string
  closedPositions: number
  wins: number
  losses: number
  winRatePct: number | null
  grossProfitUsd: number
  grossLossUsd: number
  profitFactor: number | null
  avgPnlUsd: number | null
  avgWinUsd: number | null
  avgLossUsd: number | null
  bestUsd: number | null
  worstUsd: number | null
  avgHoldMinutes: number | null
  byReason: { reason: string; count: number; pnlUsd: number }[]
  entryOrders: { filled: number; cancelled: number; open: number; fillRatePct: number | null }
}

export interface AccountBalance {
  asset: string
  displayName: string
  balance: number
  hold: number
  available: number
  priceUsd: number | null
  valueUsd: number | null
  /** Too small to sell on Kraken (below the pair's minimum order size or value). */
  isDust: boolean
  updatedAt: string
}

/** The real Kraken account, read-only, as last synced by the Trader. */
export interface Account {
  connected: boolean
  syncedAt: string | null
  error: string | null
  errorAt: string | null
  totalUsd: number
  cashUsd: number
  cashAvailableUsd: number
  unpricedAssets: number
  dustAssets: number
  dustUsd: number
  balances: AccountBalance[]
}

export interface Dashboard {
  summary: Summary
  openPositions: Position[]
  positions: Position[]
  orders: Order[]
  trades: Trade[]
  opportunities: Opportunity[]
  vetoes: Veto[]
  tokenUsage: TokenUsage[]
  marks: Record<string, Mark>
  control: Control
  stats: Stats
  account: Account
}

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`/api${path}`, { signal, headers: { Accept: 'application/json' } })
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`)
  return (await res.json()) as T
}

async function send<T>(method: 'POST' | 'PUT', path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api${path}`, {
    method,
    headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`)
  return (await res.json()) as T
}

export const setEntriesPaused = (entriesPaused: boolean) => send<Control>('PUT', '/control', { entriesPaused })

export const requestFlatten = () => send<Control>('POST', '/control/flatten')

export async function loadDashboard(signal?: AbortSignal): Promise<Dashboard> {
  const [summary, openPositions, positions, orders, trades, opportunities, vetoes, tokenUsage, marks, control, stats, account] = await Promise.all([
    get<Summary>('/summary', signal),
    get<Position[]>('/positions?status=open', signal),
    get<Position[]>('/positions', signal),
    get<Order[]>('/orders', signal),
    get<Trade[]>('/trades', signal),
    get<Opportunity[]>('/opportunities?active=true', signal),
    get<Veto[]>('/vetoes?active=true', signal),
    get<TokenUsage[]>('/token-usage', signal),
    get<Mark[]>('/marks', signal),
    get<Control>('/control', signal),
    get<Stats>('/stats', signal),
    get<Account>('/account', signal),
  ])
  return {
    summary, openPositions, positions, orders, trades, opportunities, vetoes, tokenUsage,
    marks: Object.fromEntries(marks.map((m) => [m.asset, m])),
    control, stats, account,
  }
}
