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
  EvaluateOptimizationRequest,
  OptimizationRun,
  OptimizationSubmission,
  OptimizationStatus,
  OptimizationEvaluation,
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

  async deleteBacktest(_id: string): Promise<void> {
    await delay();
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

  async evaluateOptimization(
    req: EvaluateOptimizationRequest,
  ): Promise<OptimizationEvaluation> {
    await delay(500);
    return {
      totalCombinations: 12500,
      exceedsMaxCombinations: false,
      maxCombinations: 500000,
      effectiveDimensions: 4,
      geneticConfig: req.mode === "Genetic" ? {
        populationSize: 50,
        maxGenerations: 200,
        maxEvaluations: 10000,
        mutationRate: 0.25,
      } : null,
    };
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

  // --- Validations (stubs) ---

  async getValidations(_params?: Record<string, unknown>) {
    await delay();
    return { items: [], totalCount: 0, limit: 50, offset: 0, hasMore: false };
  },

  async getValidation(_id: string) {
    await delay();
    return {
      id: "mock-validation-001",
      optimizationRunId: "mock-opt-001",
      strategyName: "MockStrategy",
      strategyVersion: "1.0",
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      durationMs: 5000,
      status: "Completed",
      thresholdProfileName: "Crypto-Standard",
      candidatesIn: 10,
      candidatesOut: 3,
      compositeScore: 72.5,
      verdict: "Green",
      verdictSummary: "Strategy PASSES validation at 73/100 — 3/10 candidates survived all stages.",
      rejections: [] as string[],
      categoryScores: { Data: 80, Stats: 75, Params: 70, WFO: 72, WFM: 65, MC: 78, SubPeriod: 68 },
      invocationCount: 1,
      errorMessage: null,
      stageResults: [],
    };
  },

  async getValidationEquity(_id: string) {
    await delay();
    return {
      trials: [{
        trialIndex: 0,
        trialId: "mock-trial-001",
        timestamps: [1700000000000, 1700086400000, 1700172800000],
        equity: [10000, 10100, 10250],
        pnlDeltas: [0, 100, 150],
      }],
      initialEquity: 10000,
    };
  },

  async getValidationStatus(_id: string) {
    await delay();
    return { id: "mock-validation-001", status: "Completed", currentStage: 8, totalStages: 8, result: null };
  },

  async runValidation(_req: { optimizationRunId: string; thresholdProfileName?: string }) {
    await delay();
    return { id: "mock-validation-new", candidateCount: 10 };
  },

  async cancelValidation(_id: string) {
    await delay();
    return { id: _id, status: "Cancelled" };
  },

  async deleteValidation(_id: string): Promise<void> {
    await delay();
  },

  async getThresholdProfiles() {
    await delay();
    return [
      { name: "Crypto-Standard", isBuiltIn: true, profileJson: "{}" },
      { name: "Crypto-Conservative", isBuiltIn: true, profileJson: "{}" },
    ];
  },
};
