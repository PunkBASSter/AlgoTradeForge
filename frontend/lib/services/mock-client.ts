// T037 - Mock client for NEXT_PUBLIC_MOCK_MODE=true development without a backend

import type {
  BacktestRun,
  BacktestSubmission,
  BacktestStatus,
  PagedResponse,
  EquityPoint,
  EventsData,
  RunBacktestRequest,
  RunOptimizationRequest,
  OptimizationRun,
  OptimizationSubmission,
  OptimizationStatus,
  StartDebugSessionRequest,
  DebugSession,
  DebugSessionStatus,
} from "@/types/api";

import type { BacktestListParams, OptimizationListParams } from "./api-client";

import strategiesData from "./mock-data/strategies.json";
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

  async getAvailableStrategies(): Promise<string[]> {
    await delay();
    return strategiesData;
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
      filtered = filtered.filter((b) => b.assetName === params.assetName);
    }
    if (params?.exchange) {
      filtered = filtered.filter((b) => b.exchange === params.exchange);
    }
    if (params?.timeFrame) {
      filtered = filtered.filter((b) => b.timeFrame === params.timeFrame);
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
      filtered = filtered.filter((o) => o.assetName === params.assetName);
    }
    if (params?.exchange) {
      filtered = filtered.filter((o) => o.exchange === params.exchange);
    }
    if (params?.timeFrame) {
      filtered = filtered.filter((o) => o.timeFrame === params.timeFrame);
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

  async getOptimizationStatus(id: string): Promise<OptimizationStatus> {
    await delay();
    const found = optimizations.items.find((o) => o.id === id);
    return { id, completedCombinations: found?.totalCombinations ?? 0, totalCombinations: found?.totalCombinations ?? 0, result: found };
  },

  async cancelOptimization(id: string): Promise<{ id: string; status: string }> {
    await delay();
    return { id, status: "Cancelled" };
  },

  // --- Debug sessions ---

  async createDebugSession(
    req: StartDebugSessionRequest,
  ): Promise<DebugSession> {
    await delay();
    return {
      sessionId: "mock-debug-session-001",
      assetName: req.assetName,
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
