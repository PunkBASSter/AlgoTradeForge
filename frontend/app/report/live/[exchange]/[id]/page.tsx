"use client";

import React from "react";
import { useRouter } from "next/navigation";
import {
  useLiveSessionDetail,
  useLiveSessionData,
  useStopLiveSession,
} from "@/hooks/use-live-sessions";
import { StatusBadge } from "@/components/ui/status-badge";
import { StatItem } from "@/components/ui/stat-item";
import { Skeleton } from "@/components/ui/skeleton";
import { CandlestickChart } from "@/components/features/charts/candlestick-chart";
import type {
  LiveFill,
  LiveExchangeTrade,
  LivePendingOrder,
  LiveLastBar,
} from "@/types/api";

function formatNumber(value: number, decimals = 2): string {
  return value.toLocaleString(undefined, {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
}

export default function LiveSessionPage({
  params,
}: {
  params: Promise<{ exchange: string; id: string }>;
}) {
  const { exchange, id } = React.use(params);
  const router = useRouter();

  const { data: session, isLoading, error } = useLiveSessionDetail(id);
  const isRunning = session?.status === "Running";
  const { data: sessionData } = useLiveSessionData(id, isRunning || !!session);
  const stopMutation = useStopLiveSession();

  const handleTerminate = () => {
    if (!confirm("Stop this live trading session? This cannot be undone."))
      return;
    const strategy = session?.strategyName ?? "all";
    stopMutation.mutate(id, {
      onSuccess: () => router.push(`/${encodeURIComponent(strategy)}/live`),
    });
  };

  if (error) {
    return (
      <div className="p-6 flex flex-col items-center justify-center gap-4">
        <div className="p-6 bg-bg-panel border border-accent-red rounded-lg text-center max-w-md">
          <h2 className="text-lg font-semibold text-accent-red mb-2">
            Failed to load session
          </h2>
          <p className="text-sm text-text-secondary">{error.message}</p>
        </div>
      </div>
    );
  }

  if (isLoading || !session) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton variant="line" width="300px" />
        <Skeleton variant="rect" height="80px" />
        <Skeleton variant="rect" height="200px" />
      </div>
    );
  }

  const candles = sessionData?.candles ?? [];
  const fills = sessionData?.fills ?? [];
  const pendingOrders = sessionData?.pendingOrders ?? [];
  const account = sessionData?.account;
  const lastBars = sessionData?.lastBars ?? [];
  const exchangeTrades = sessionData?.exchangeTrades ?? [];
  const latestClose =
    candles.length > 0 ? candles[candles.length - 1].close : null;

  return (
    <div className="p-6 space-y-6">
      {/* Title + Terminate */}
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-bold text-text-primary">
            Live Session: {session.strategyName}
            <span className="ml-2 text-sm font-normal text-text-muted">
              v{session.strategyVersion}
            </span>
          </h1>
          <p className="text-sm text-text-secondary mt-1">
            {session.assetName} / {decodeURIComponent(exchange)}
          </p>
        </div>
        <button
          type="button"
          onClick={handleTerminate}
          disabled={stopMutation.isPending}
          className="shrink-0 px-3 py-1.5 rounded-md text-sm font-medium bg-accent-red text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {stopMutation.isPending ? "Stopping..." : "Terminate Session"}
        </button>
      </div>

      {/* Session Info */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
          <StatItem
            label="Status"
            value={<StatusBadge status={session.status} />}
          />
          <StatItem
            label="Session ID"
            value={
              <span className="text-sm font-mono">
                {session.sessionId.substring(0, 12)}...
              </span>
            }
          />
          <StatItem label="Account" value={session.accountName} />
          <StatItem
            label="Started"
            value={new Date(session.startedAt).toLocaleString()}
          />
          <StatItem label="Exchange" value={session.exchange} />
          <StatItem label="Asset" value={session.assetName} />
          <StatItem label="Strategy" value={session.strategyName} />
        </div>
      </div>

      {/* Market Data */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted">
            Market Data
          </h2>
          {latestClose !== null && (
            <span className="text-lg font-bold text-text-primary">
              {formatNumber(latestClose)}
            </span>
          )}
        </div>
        {candles.length > 0 ? (
          <CandlestickChart bulkCandles={candles} />
        ) : (
          <p className="text-sm text-text-muted">
            Waiting for candle data...
          </p>
        )}
        {lastBars.length > 0 && <LastBarsTable lastBars={lastBars} />}
      </div>

      {/* Recent Orders & Fills */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
          Recent Orders
        </h2>
        {pendingOrders.length > 0 && (
          <PendingOrdersTable orders={pendingOrders} />
        )}
        {fills.length > 0 && (
          <div className="mb-4">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-text-muted mb-2">
              Session Fills
            </h3>
            <FillsTable fills={fills} />
          </div>
        )}
        {exchangeTrades.length > 0 && (
          <div>
            <h3 className="text-xs font-semibold uppercase tracking-wider text-text-muted mb-2">
              Exchange Trade History
            </h3>
            <ExchangeTradesTable trades={exchangeTrades} />
          </div>
        )}
        {fills.length === 0 && exchangeTrades.length === 0 && (
          <p className="text-sm text-text-muted">No orders yet.</p>
        )}
      </div>

      {/* Account Funds */}
      <div className="rounded-lg border border-border-default bg-bg-panel p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-text-muted mb-3">
          Account Funds
        </h2>
        {account ? (
          <>
            <div className="grid grid-cols-3 sm:grid-cols-4 gap-4 mb-4">
              <StatItem
                label="Exchange Balance"
                value={formatNumber(account.exchangeBalance)}
              />
              <StatItem
                label="Initial Cash"
                value={formatNumber(account.initialCash)}
              />
              <StatItem
                label="Current Cash"
                value={formatNumber(account.cash)}
              />
            </div>
            {account.positions.length > 0 ? (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-text-muted border-b border-border-default">
                    <th className="pb-2 font-medium">Symbol</th>
                    <th className="pb-2 font-medium text-right">Qty</th>
                    <th className="pb-2 font-medium text-right">Avg Entry</th>
                    <th className="pb-2 font-medium text-right">
                      Realized PnL
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {account.positions.map((pos) => (
                    <tr
                      key={pos.symbol}
                      className="border-b border-border-default last:border-0"
                    >
                      <td className="py-2 font-mono">{pos.symbol}</td>
                      <td className="py-2 text-right">{pos.quantity}</td>
                      <td className="py-2 text-right">
                        {formatNumber(pos.averageEntryPrice)}
                      </td>
                      <td
                        className={`py-2 text-right ${pos.realizedPnl >= 0 ? "text-accent-green" : "text-accent-red"}`}
                      >
                        {formatNumber(pos.realizedPnl)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : (
              <p className="text-sm text-text-muted">No open positions.</p>
            )}
          </>
        ) : (
          <p className="text-sm text-text-muted">
            Account data loading...
          </p>
        )}
      </div>
    </div>
  );
}

function LastBarsTable({ lastBars }: { lastBars: LiveLastBar[] }) {
  return (
    <div className="mt-3">
      <h3 className="text-xs font-semibold uppercase tracking-wider text-text-muted mb-2">
        Last Bar per Subscription
      </h3>
      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-text-muted border-b border-border-default">
            <th className="pb-2 font-medium">Symbol</th>
            <th className="pb-2 font-medium">TimeFrame</th>
            <th className="pb-2 font-medium text-right">Open</th>
            <th className="pb-2 font-medium text-right">High</th>
            <th className="pb-2 font-medium text-right">Low</th>
            <th className="pb-2 font-medium text-right">Close</th>
            <th className="pb-2 font-medium text-right">Volume</th>
          </tr>
        </thead>
        <tbody>
          {lastBars.map((bar) => (
            <tr
              key={`${bar.symbol}-${bar.timeFrame}`}
              className="border-b border-border-default last:border-0"
            >
              <td className="py-2 font-mono">{bar.symbol}</td>
              <td className="py-2">{bar.timeFrame}</td>
              <td className="py-2 text-right">{formatNumber(bar.open)}</td>
              <td className="py-2 text-right">{formatNumber(bar.high)}</td>
              <td className="py-2 text-right">{formatNumber(bar.low)}</td>
              <td className="py-2 text-right">{formatNumber(bar.close)}</td>
              <td className="py-2 text-right">
                {bar.volume.toLocaleString()}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function PendingOrdersTable({ orders }: { orders: LivePendingOrder[] }) {
  return (
    <div className="mb-4">
      <h3 className="text-xs font-semibold uppercase tracking-wider text-text-muted mb-2">
        Pending Orders
      </h3>
      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-text-muted border-b border-border-default">
            <th className="pb-2 font-medium">ID</th>
            <th className="pb-2 font-medium">Side</th>
            <th className="pb-2 font-medium">Type</th>
            <th className="pb-2 font-medium text-right">Qty</th>
            <th className="pb-2 font-medium text-right">Limit</th>
            <th className="pb-2 font-medium text-right">Stop</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => (
            <tr
              key={order.id}
              className="border-b border-border-default last:border-0"
            >
              <td className="py-2 font-mono">{order.id}</td>
              <td
                className={`py-2 ${order.side === "Buy" ? "text-accent-green" : "text-accent-red"}`}
              >
                {order.side}
              </td>
              <td className="py-2">
                <StatusBadge status={order.type} />
              </td>
              <td className="py-2 text-right">{order.quantity}</td>
              <td className="py-2 text-right">
                {order.limitPrice != null ? formatNumber(order.limitPrice) : "-"}
              </td>
              <td className="py-2 text-right">
                {order.stopPrice != null ? formatNumber(order.stopPrice) : "-"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function FillsTable({ fills }: { fills: LiveFill[] }) {
  const reversed = [...fills].reverse();
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="text-left text-text-muted border-b border-border-default">
          <th className="pb-2 font-medium">Order</th>
          <th className="pb-2 font-medium">Time</th>
          <th className="pb-2 font-medium">Side</th>
          <th className="pb-2 font-medium text-right">Qty</th>
          <th className="pb-2 font-medium text-right">Price</th>
          <th className="pb-2 font-medium text-right">Commission</th>
        </tr>
      </thead>
      <tbody>
        {reversed.map((fill, i) => (
          <tr
            key={`${fill.orderId}-${i}`}
            className="border-b border-border-default last:border-0"
          >
            <td className="py-2 font-mono text-text-muted">{fill.orderId}</td>
            <td className="py-2 text-text-secondary">
              {new Date(fill.timestamp).toLocaleString()}
            </td>
            <td
              className={`py-2 ${fill.side === "Buy" ? "text-accent-green" : "text-accent-red"}`}
            >
              {fill.side}
            </td>
            <td className="py-2 text-right">{fill.quantity}</td>
            <td className="py-2 text-right">{formatNumber(fill.price)}</td>
            <td className="py-2 text-right">{formatNumber(fill.commission)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

interface OrderGroup {
  orderId: number;
  side: string;
  timestamp: string;
  fills: LiveExchangeTrade[];
  totalQty: number;
  avgPrice: number;
  totalValue: number;
  totalCommission: number;
  commissionAsset: string;
}

function groupTradesByOrder(trades: LiveExchangeTrade[]): OrderGroup[] {
  const map = new Map<number, LiveExchangeTrade[]>();
  for (const trade of trades) {
    const existing = map.get(trade.orderId);
    if (existing) {
      existing.push(trade);
    } else {
      map.set(trade.orderId, [trade]);
    }
  }

  const groups: OrderGroup[] = [];
  for (const [orderId, fills] of map) {
    const totalQty = fills.reduce((sum, f) => sum + f.quantity, 0);
    const totalValue = fills.reduce((sum, f) => sum + f.quantity * f.price, 0);
    const avgPrice = totalQty > 0 ? totalValue / totalQty : 0;
    const totalCommission = fills.reduce((sum, f) => sum + f.commission, 0);
    groups.push({
      orderId,
      side: fills[0].side,
      timestamp: fills[0].timestamp,
      fills,
      totalQty,
      avgPrice,
      totalValue,
      totalCommission,
      commissionAsset: fills[0].commissionAsset,
    });
  }

  // Most recent order first
  groups.reverse();
  return groups;
}

function formatCommission(commission: number, asset: string): string {
  if (commission === 0) return "\u2014";
  return `${commission.toFixed(8).replace(/0+$/, "").replace(/\.$/, "")} ${asset}`;
}

function ExchangeTradesTable({ trades }: { trades: LiveExchangeTrade[] }) {
  const groups = groupTradesByOrder(trades);
  const [expandedOrders, setExpandedOrders] = React.useState<Set<number>>(
    new Set(),
  );

  const toggleOrder = (orderId: number) => {
    setExpandedOrders((prev) => {
      const next = new Set(prev);
      if (next.has(orderId)) {
        next.delete(orderId);
      } else {
        next.add(orderId);
      }
      return next;
    });
  };

  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="text-left text-text-muted border-b border-border-default">
          <th className="pb-2 font-medium">Order</th>
          <th className="pb-2 font-medium">Time</th>
          <th className="pb-2 font-medium">Side</th>
          <th className="pb-2 font-medium text-right">Fills</th>
          <th className="pb-2 font-medium text-right">Total Qty</th>
          <th className="pb-2 font-medium text-right">Avg Price</th>
          <th className="pb-2 font-medium text-right">Value</th>
          <th className="pb-2 font-medium text-right">Commission</th>
        </tr>
      </thead>
      <tbody>
        {groups.map((group) => {
          const isMultiFill = group.fills.length > 1;
          const isExpanded = expandedOrders.has(group.orderId);

          return (
            <React.Fragment key={group.orderId}>
              {/* Summary row */}
              <tr
                className={`border-b border-border-default ${isMultiFill ? "cursor-pointer hover:bg-bg-surface" : ""}`}
                onClick={isMultiFill ? () => toggleOrder(group.orderId) : undefined}
              >
                <td className="py-2 font-mono text-text-muted">
                  {isMultiFill && (
                    <span className="inline-block w-4 text-text-muted">
                      {isExpanded ? "\u25BC" : "\u25B6"}
                    </span>
                  )}
                  {group.orderId}
                </td>
                <td className="py-2 text-text-secondary">
                  {new Date(group.timestamp).toLocaleString()}
                </td>
                <td
                  className={`py-2 ${group.side === "Buy" ? "text-accent-green" : "text-accent-red"}`}
                >
                  {group.side}
                </td>
                <td className="py-2 text-right">
                  {isMultiFill ? group.fills.length : ""}
                </td>
                <td className="py-2 text-right">{group.totalQty}</td>
                <td className="py-2 text-right">
                  {formatNumber(group.avgPrice)}
                </td>
                <td className="py-2 text-right">
                  {formatNumber(group.totalValue)}
                </td>
                <td className="py-2 text-right">
                  {formatCommission(group.totalCommission, group.commissionAsset)}
                </td>
              </tr>

              {/* Expanded sub-rows for multi-fill orders */}
              {isMultiFill &&
                isExpanded &&
                group.fills.map((fill, i) => {
                  const isLast = i === group.fills.length - 1;
                  const prefix = isLast ? "\u2514\u2500" : "\u251C\u2500";
                  return (
                    <tr
                      key={`${fill.orderId}-${i}`}
                      className="border-b border-border-default last:border-0 bg-bg-surface/50"
                    >
                      <td className="py-1.5 pl-6 text-text-muted font-mono text-xs">
                        {prefix}
                      </td>
                      <td className="py-1.5 text-text-secondary text-xs">
                        {new Date(fill.timestamp).toLocaleString()}
                      </td>
                      <td className="py-1.5" />
                      <td className="py-1.5" />
                      <td className="py-1.5 text-right text-xs">
                        {fill.quantity}
                      </td>
                      <td className="py-1.5 text-right text-xs">
                        {formatNumber(fill.price)}
                      </td>
                      <td className="py-1.5 text-right text-xs">
                        {formatNumber(fill.quantity * fill.price)}
                      </td>
                      <td className="py-1.5 text-right text-xs">
                        {formatCommission(fill.commission, fill.commissionAsset)}
                      </td>
                    </tr>
                  );
                })}
            </React.Fragment>
          );
        })}
      </tbody>
    </table>
  );
}
