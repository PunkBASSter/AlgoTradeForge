// T037 - Mock client for NEXT_PUBLIC_MOCK_MODE=true development without a backend

import type {
  BacktestRun,
  BacktestSubmission,
  BacktestStatus,
  PagedResponse,
  EquityPoint,
  TradePoint,
  EventsData,
  RunBacktestRequest,
  RunOptimizationRequest,
  RunGeneticOptimizationRequest,
  OptimizationRun,
  OptimizationSubmission,
  OptimizationStatus,
  StartDebugSessionRequest,
  StartLiveSessionRequest,
  LiveSessionSubmission,
  LiveSession,
  LiveSessionListResponse,
  LiveSessionData,
  DebugSession,
  DebugSessionStatus,
  StrategyDescriptor,
} from "@/types/api";

import type { BacktestListParams, OptimizationListParams } from "./api-client";

import strategiesData from "./mock-data/strategies.json";

const mockStrategyDescriptors: StrategyDescriptor[] = strategiesData.map((name) => ({
  name,
  parameterDefaults: {},
  optimizationAxes: [],
  backtestTemplate: {},
  optimizationTemplate: {},
  liveSessionTemplate: {},
  debugSessionTemplate: {},
  geneticOptimizationTemplate: {},
}));
import backtestsData from "./mock-data/backtests.json";
import optimizationsData from "./mock-data/optimizations.json";
import equityData from "./mock-data/equity.json";
import eventsData from "./mock-data/events.json";
import { debugEventLines } from "./mock-data/debug-events";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async function delay(ms = 300): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// ---------------------------------------------------------------------------
// Typed references to imported JSON
// ---------------------------------------------------------------------------

const backtests = backtestsData as PagedResponse<BacktestRun>;
const optimizations = optimizationsData as PagedResponse<OptimizationRun>;
const equity = equityData as EquityPoint[];
const events = eventsData as EventsData;

// ---------------------------------------------------------------------------
// Mock client
// ---------------------------------------------------------------------------

export const mockClient: typeof import("./api-client").apiClient & {
  getMockDebugEvents(): string[];
} = {
  // --- Strategies ---

  async getStrategies(): Promise<string[]> {
    await delay();
    return strategiesData;
  },

  async getAvailableStrategies(): Promise<StrategyDescriptor[]> {
    await delay();
    return mockStrategyDescriptors;
  },

  // --- Backtests ---

  async getBacktests(
    params?: BacktestListParams,
  ): Promise<PagedResponse<BacktestRun>> {
    await delay();
    let filtered = backtests.items;

    if (params?.strategyName) {
      filtered = filtered.filter(
        (b) => b.strategyName === params.strategyName,
      );
    }
    if (params?.assetName) {
      filtered = filtered.filter((b) => b.dataSubscription.assetName === params.assetName);
    }
    if (params?.exchange) {
      filtered = filtered.filter((b) => b.dataSubscription.exchange === params.exchange);
    }
    if (params?.timeFrame) {
      filtered = filtered.filter((b) => b.dataSubscription.timeFrame === params.timeFrame);
    }
    if (params?.standaloneOnly) {
      filtered = filtered.filter((b) => b.runMode === "standalone");
    }

    const offset = params?.offset ?? 0;
    const limit = params?.limit ?? 12;
    const page = filtered.slice(offset, offset + limit);

    return {
      items: page,
      totalCount: filtered.length,
      limit,
      offset,
      hasMore: offset + limit < filtered.length,
    };
  },

  async getBacktest(id: string): Promise<BacktestRun> {
    await delay();
    const found = backtests.items.find((b) => b.id === id);
    if (!found) {
      throw new Error(`API error 404: Backtest ${id} not found`);
    }
    return found;
  },

  async getBacktestEquity(_id: string): Promise<EquityPoint[]> {
    await delay();
    return equity;
  },

  async getBacktestTrades(_id: string): Promise<TradePoint[]> {
    await delay();
    return [];
  },

  async getBacktestEvents(_id: string): Promise<EventsData> {
    await delay();
    return events;
  },

  async runBacktest(_req: RunBacktestRequest): Promise<BacktestSubmission> {
    await delay(800);
    return { id: backtests.items[0].id, totalBars: backtests.items[0].totalBars };
  },

  async getBacktestStatus(id: string): Promise<BacktestStatus> {
    await delay();
    const found = backtests.items.find((b) => b.id === id);
    return { id, processedBars: found?.totalBars ?? 0, totalBars: found?.totalBars ?? 0, result: found };
  },

  async cancelBacktest(id: string): Promise<{ id: string; status: string }> {
    await delay();
    return { id, status: "Cancelled" };
  },

  // --- Optimizations ---

  async getOptimizations(
    params?: OptimizationListParams,
  ): Promise<PagedResponse<OptimizationRun>> {
    await delay();
    let filtered = optimizations.items;

    if (params?.strategyName) {
      filtered = filtered.filter(
        (o) => o.strategyName === params.strategyName,
      );
    }
    if (params?.assetName) {
      filtered = filtered.filter((o) => o.dataSubscription.assetName === params.assetName);
    }
    if (params?.exchange) {
      filtered = filtered.filter((o) => o.dataSubscription.exchange === params.exchange);
    }
    if (params?.timeFrame) {
      filtered = filtered.filter((o) => o.dataSubscription.timeFrame === params.timeFrame);
    }

    const offset = params?.offset ?? 0;
    const limit = params?.limit ?? 12;
    const page = filtered.slice(offset, offset + limit);

    return {
      items: page,
      totalCount: filtered.length,
      limit,
      offset,
      hasMore: offset + limit < filtered.length,
    };
  },

  async getOptimization(id: string): Promise<OptimizationRun> {
    await delay();
    const found = optimizations.items.find((o) => o.id === id);
    if (!found) {
      throw new Error(`API error 404: Optimization ${id} not found`);
    }
    return found;
  },

  async runOptimization(
    _req: RunOptimizationRequest,
  ): Promise<OptimizationSubmission> {
    await delay(1200);
    return { id: optimizations.items[0].id, totalCombinations: optimizations.items[0].totalCombinations };
  },

  async runGeneticOptimization(
    _req: RunGeneticOptimizationRequest,
  ): Promise<OptimizationSubmission> {
    await delay(1200);
    return { id: optimizations.items[0].id, totalCombinations: optimizations.items[0].totalCombinations };
  },

  async getOptimizationStatus(id: string): Promise<OptimizationStatus> {
    await delay();
    const found = optimizations.items.find((o) => o.id === id);
    return { id, completedCombinations: found?.totalCombinations ?? 0, totalCombinations: found?.totalCombinations ?? 0, result: found };
  },

  async cancelOptimization(id: string): Promise<{ id: string; status: string }> {
    await delay();
    return { id, status: "Cancelled" };
  },

  async deleteOptimization(_id: string): Promise<void> {
    await delay();
  },

  // --- Live sessions ---

  async getLiveSessions(): Promise<LiveSessionListResponse> {
    await delay();
    return {
      sessions: [
        {
          sessionId: "mock-live-session-001",
          status: "Running",
          strategyName: "BuyAndHold",
          strategyVersion: "1.0",
          exchange: "Binance",
          assetName: "BTCUSDT",
          accountName: "paper",
          startedAt: new Date().toISOString(),
        },
      ],
    };
  },

  async getLiveSession(_id: string): Promise<LiveSession> {
    await delay();
    return {
      sessionId: "mock-live-session-001",
      status: "Running",
      strategyName: "BuyAndHold",
      strategyVersion: "1.0",
      exchange: "Binance",
      assetName: "BTCUSDT",
      accountName: "paper",
      startedAt: new Date().toISOString(),
    };
  },

  async startLiveSession(_req: StartLiveSessionRequest): Promise<LiveSessionSubmission> {
    await delay(500);
    return { sessionId: "mock-live-session-002" };
  },

  async getLiveSessionData(_id: string): Promise<LiveSessionData> {
    await delay();
    const now = Date.now();
    return {
      candles: Array.from({ length: 30 }, (_, i) => ({
        time: now - (30 - i) * 60_000,
        open: 65000 + i * 10,
        high: 65100 + i * 10,
        low: 64900 + i * 10,
        close: 65050 + i * 10,
        volume: 100 + i,
      })),
      fills: [],
      pendingOrders: [],
      account: { initialCash: 100000, cash: 100000, exchangeBalance: 100000, positions: [] },
      timeFrame: "00:01:00",
      lastBars: [{ symbol: "BTCUSDT", timeFrame: "00:01:00", time: now, open: 65290, high: 65390, low: 65190, close: 65340, volume: 129 }],
      exchangeTrades: [],
    };
  },

  async stopLiveSession(_id: string): Promise<void> {
    await delay();
  },

  // --- Debug sessions ---

  async createDebugSession(
    req: StartDebugSessionRequest,
  ): Promise<DebugSession> {
    await delay();
    return {
      sessionId: "mock-debug-session-001",
      assetName: req.dataSubscription.assetName,
      strategyName: req.strategyName,
      createdAt: new Date().toISOString(),
    };
  },

  async getDebugSession(_id: string): Promise<DebugSessionStatus> {
    await delay();
    return {
      sessionId: "mock-debug-session-001",
      isRunning: true,
      lastSnapshot: {
        type: "snapshot",
        sessionActive: true,
        sequenceNumber: 27,
        timestampMs: 1749427200000,
        subscriptionIndex: 0,
        isExportableSubscription: true,
        fillsThisBar: 1,
        portfolioEquity: 1000013933500,
      },
      createdAt: new Date().toISOString(),
    };
  },

  async deleteDebugSession(_id: string): Promise<void> {
    await delay();
  },

  getDebugWebSocketUrl(_sessionId: string): string {
    return "";
  },

  getMockDebugEvents(): string[] {
    return debugEventLines;
  },
};
