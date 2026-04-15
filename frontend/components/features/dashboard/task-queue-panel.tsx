"use client";

import { useState } from "react";
import { useTaskQueue, useCancelTask, usePurgeQueue } from "@/hooks/use-task-queue";
import { StatusBadge } from "@/components/ui/status-badge";
import { Button } from "@/components/ui/button";
import type { TaskQueueItem } from "@/types/task-queue";

function ProgressBar({ processed, total }: { processed: number; total: number }) {
  const pct = total > 0 ? Math.min((processed / total) * 100, 100) : 0;
  return (
    <div className="w-full bg-bg-surface rounded-full h-1.5 overflow-hidden">
      <div
        className="h-full bg-accent-blue rounded-full transition-all duration-500"
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}

function TaskRow({
  task,
  onCancel,
  cancelling,
}: {
  task: TaskQueueItem;
  onCancel: (id: string) => void;
  cancelling: boolean;
}) {
  const statusDisplay = task.status === "InProgress" ? "Running" : task.status;
  const typeLabel = task.type === "Optimization" ? "Opt" : "Val";

  return (
    <div className="px-3 py-2 rounded border border-border-default bg-bg-panel space-y-1.5">
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <StatusBadge status={statusDisplay} />
          <span className="text-xs font-medium text-text-primary truncate">
            {typeLabel} #{task.dssIndex}
          </span>
          <span className="text-xs text-text-muted truncate">{task.dssLabel}</span>
        </div>
        <button
          onClick={() => onCancel(task.id)}
          disabled={cancelling}
          className="text-xs text-text-muted hover:text-accent-red transition-colors disabled:opacity-50 shrink-0"
        >
          Cancel
        </button>
      </div>

      {task.progress && (
        <div className="space-y-1">
          <ProgressBar processed={task.progress.processed} total={task.progress.total} />
          <p className="text-xs text-text-muted">
            {task.progress.processed.toLocaleString()} / {task.progress.total.toLocaleString()}
            {task.type === "Optimization" ? " combinations" : " stages"}
          </p>
        </div>
      )}
    </div>
  );
}

export function TaskQueuePanel() {
  const { data, isLoading } = useTaskQueue();
  const cancelTask = useCancelTask();
  const purgeQueue = usePurgeQueue();
  const [confirmPurge, setConfirmPurge] = useState(false);

  // Render nothing when queue is empty and not loading
  if (!isLoading && (!data || data.activeTasks.length === 0)) return null;

  const handleCancel = (taskId: string) => {
    cancelTask.mutate(taskId);
  };

  const handlePurge = () => {
    if (!confirmPurge) {
      setConfirmPurge(true);
      return;
    }
    purgeQueue.mutate(undefined, {
      onSettled: () => setConfirmPurge(false),
    });
  };

  const pendingCount = data?.pendingCount ?? 0;

  return (
    <div className="rounded-lg border border-border-default bg-bg-base p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold text-text-primary">
          Task Queue
          {pendingCount > 0 && (
            <span className="ml-2 text-xs font-normal text-text-muted">
              ({pendingCount} pending)
            </span>
          )}
        </h3>
        {pendingCount > 0 && (
          <Button
            variant={confirmPurge ? "danger" : "ghost"}
            className="text-xs px-2 py-1"
            loading={purgeQueue.isPending}
            onClick={handlePurge}
          >
            {confirmPurge ? "Confirm purge" : "Purge pending"}
          </Button>
        )}
      </div>

      {isLoading ? (
        <div className="space-y-2">
          <div className="h-12 bg-bg-surface animate-pulse rounded" />
          <div className="h-12 bg-bg-surface animate-pulse rounded" />
        </div>
      ) : (
        <div className="space-y-2">
          {data?.activeTasks.map((task) => (
            <TaskRow
              key={task.id}
              task={task}
              onCancel={handleCancel}
              cancelling={cancelTask.isPending}
            />
          ))}
        </div>
      )}
    </div>
  );
}
