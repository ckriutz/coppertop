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

export interface Dashboard {
  summary: Summary
  openPositions: Position[]
  positions: Position[]
  orders: Order[]
  trades: Trade[]
  opportunities: Opportunity[]
  vetoes: Veto[]
  tokenUsage: TokenUsage[]
}

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`/api${path}`, { signal, headers: { Accept: 'application/json' } })
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`)
  return (await res.json()) as T
}

export async function loadDashboard(signal?: AbortSignal): Promise<Dashboard> {
  const [summary, openPositions, positions, orders, trades, opportunities, vetoes, tokenUsage] = await Promise.all([
    get<Summary>('/summary', signal),
    get<Position[]>('/positions?status=open', signal),
    get<Position[]>('/positions', signal),
    get<Order[]>('/orders', signal),
    get<Trade[]>('/trades', signal),
    get<Opportunity[]>('/opportunities?active=true', signal),
    get<Veto[]>('/vetoes?active=true', signal),
    get<TokenUsage[]>('/token-usage', signal),
  ])
  return { summary, openPositions, positions, orders, trades, opportunities, vetoes, tokenUsage }
}
