export interface TaskProgressDto {
  processed: number;
  total: number;
}

export interface TaskQueueItem {
  id: string;
  jobId: string;
  type: "Optimization" | "Validation";
  dssIndex: number;
  dssLabel: string;
  runId: string;
  status: "Pending" | "InProgress" | "Completed" | "Failed" | "Cancelled";
  enqueuedAt: string;
  progress: TaskProgressDto | null;
}

export interface TaskQueueSnapshot {
  activeTasks: TaskQueueItem[];
  pendingCount: number;
  inProgressTask: string | null;
}

export interface CancelTaskResponse {
  taskId: string;
  status: string;
  cascadeCancelled: string[];
}

export interface PurgeResponse {
  purgedCount: number;
  purgedTaskIds: string[];
}
