# Feature Specification: Optimization Task Queue

**Feature Branch**: `028-dss-optimization-split`  
**Created**: 2026-04-14  
**Status**: Draft  
**Input**: User description: "Transform the existing optimization and validation flow to be more predictable and transparent, handling a single compute-heavy task at a time, reusing cache between optimization and validation, and persisting trial data during the transition between phases."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Submit Optimization with Automatic Validation (Priority: P1)

A user configures an optimization job from the UI: selects strategy, parameter axes, DSS subscriptions, optimization method (grid or genetic), and optionally enables validation with a threshold profile. Upon submission, the system enqueues all tasks (optimization + validation per DSS) into a central task queue and begins processing them one compute-heavy task at a time.

**Why this priority**: This is the core workflow transformation. Without the sequential task queue, the system cannot guarantee predictable resource usage or cache reuse between phases.

**Independent Test**: Can be tested by submitting a multi-DSS optimization with validation enabled and verifying that tasks execute one at a time in the correct order (DSS#1 opt, DSS#1 val, DSS#2 opt, DSS#2 val, ...) while reusing trial data from optimization in the validation phase.

**Acceptance Scenarios**:

1. **Given** a strategy with 2 DSS and validation enabled, **When** the user submits an optimization, **Then** the system enqueues 4 tasks (opt#1, val#1, opt#2, val#2) and processes them sequentially, one compute-heavy task at a time.
2. **Given** an optimization task for DSS#1 completes, **When** the validation task for DSS#1 begins, **Then** the validation uses the in-memory trial data from the optimization phase without re-loading from the database.
3. **Given** an optimization task for DSS#1 completes, **When** the system transitions to validation for DSS#1, **Then** the optimization trial data is persisted to the database during this transition window (I/O happens while the next compute task has not yet started, or concurrently with validation compute).
4. **Given** a multi-DSS optimization with validation disabled, **When** the user submits, **Then** only optimization tasks are enqueued (no validation tasks).

---

### User Story 2 - Monitor Task Queue Progress (Priority: P2)

A user views a task queue panel in the UI that shows all pending and in-progress tasks. Each task displays its DSS context, current status, type (optimization or validation), and intra-task progress. Completed tasks are removed from the queue and accessible through existing results pages. The user can see at a glance what the system is working on and what is coming next.

**Why this priority**: Transparency and predictability are core goals. Users need to see the queue state to trust the system's behavior and plan their work.

**Independent Test**: Can be tested by submitting an optimization and observing the task queue UI updating in real-time as tasks transition between states.

**Acceptance Scenarios**:

1. **Given** a multi-DSS optimization is submitted, **When** the user views the task queue, **Then** they see all tasks listed with their status (pending, in-progress, completed) and DSS context.
2. **Given** a task transitions from pending to in-progress, **When** the user views the task queue, **Then** the status update is reflected without requiring a page refresh (polling or server-sent events).
3. **Given** a task completes, **When** the user views the task queue, **Then** the task is removed from the queue panel. The completed results are accessible through the existing optimization/validation results pages.
4. **Given** an optimization task is in-progress, **When** the user views the task queue, **Then** they see the current combination progress (e.g., "1,500 / 3,000 combinations").
5. **Given** a validation task is in-progress, **When** the user views the task queue, **Then** they see the current pipeline stage progress (e.g., "stage 3 / 8").

---

### User Story 3 - Cancel Individual Tasks and Purge Pending (Priority: P2)

A user can cancel an individual in-progress or pending task, or purge all pending tasks from the queue. Cancelling an in-progress task stops the current computation gracefully. Cancelling a pending task removes it from the queue before it starts. Purging removes all pending tasks at once.

**Why this priority**: Users need control over long-running jobs, especially when they realize parameters are wrong or priorities change.

**Independent Test**: Can be tested by submitting a multi-DSS optimization, cancelling one pending task, and verifying the remaining tasks continue to execute.

**Acceptance Scenarios**:

1. **Given** a task is in-progress, **When** the user cancels it, **Then** the current computation stops gracefully and the task status becomes "cancelled".
2. **Given** a task is pending in the queue, **When** the user cancels it, **Then** the task is removed from the queue and its status becomes "cancelled" without affecting other tasks.
3. **Given** multiple tasks are pending, **When** the user clicks "purge pending", **Then** all pending tasks are removed from the queue and marked as cancelled.
4. **Given** a DSS optimization task is cancelled, **When** the corresponding validation task for the same DSS is still pending, **Then** the validation task is also cancelled automatically (no point validating an incomplete optimization).

---

### User Story 4 - Configure Max Threads (Priority: P3)

A user sets the maximum number of threads for parallel execution within a single compute task. The default (0) uses the system's CPU core count. A positive integer caps the thread count at the specified value, bounded by the system's CPU core count.

**Why this priority**: Power users need to control resource usage, e.g., leaving cores free for other work or limiting memory pressure.

**Independent Test**: Can be tested by submitting an optimization with max threads set to 2 and verifying that only 2 parallel workers execute within the compute task.

**Acceptance Scenarios**:

1. **Given** max threads is set to 0 (default), **When** an optimization runs, **Then** it uses the system's CPU core count for parallel execution within that task.
2. **Given** max threads is set to a positive integer N, **When** an optimization runs, **Then** it uses at most N parallel workers, capped at the system's CPU core count.
3. **Given** max threads is set to a value exceeding the system's CPU core count, **When** the user submits, **Then** the backend clamps the value to the system's CPU core count.

---

### Edge Cases

- What happens when the queue consumer crashes mid-task? The in-progress task should be marked as failed, and the remaining pending tasks should remain in the queue for the consumer to resume when it restarts.
- What happens when a new optimization is submitted while the queue is already processing? The new tasks are appended to the queue and will execute after all currently queued tasks complete.
- What happens when validation is enabled but optimization produces zero passing trials? The validation task should complete immediately with an empty result rather than failing.
- What happens when the application restarts? The ephemeral queue is lost. Any in-progress tasks should be marked as failed. Pending tasks are not recovered (they were never persisted). Completed results remain in the database.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST process compute-heavy tasks (optimization and validation) one at a time through a central task queue, ensuring no two compute tasks run concurrently.
- **FR-002**: System MUST enqueue optimization and validation tasks in paired order per DSS: optimization first, then validation for the same DSS, before proceeding to the next DSS.
- **FR-003**: System MUST reuse the in-memory trial data from a completed optimization when starting the validation for the same DSS, without re-loading from the database.
- **FR-004**: System MUST persist optimization trial data to the database during the transition between optimization completion and validation start for each DSS (I/O during the handoff window or concurrently with the next phase).
- **FR-005**: System MUST allow users to cancel individual tasks (both in-progress and pending) and to purge all pending tasks.
- **FR-006**: System MUST expose the queue state (pending and in-progress tasks with DSS context, task type, and intra-task progress) through an API for UI consumption. Completed tasks are removed from the queue and accessible via existing results endpoints.
- **FR-007**: System MUST accept a "max threads" parameter (0 = system CPU count, positive integer capped at CPU count) controlling the parallelism within each compute task.
- **FR-008**: System MUST automatically cancel a pending validation task if its corresponding optimization task is cancelled or fails.
- **FR-009**: System MUST support both grid (brute-force) and genetic optimization methods through the same queue mechanism.
- **FR-010**: System MUST allow the user to optionally enable or disable validation as part of the optimization submission.
- **FR-011**: System MUST allow the user to select a validation threshold profile when validation is enabled.
- **FR-012**: System MUST release per-DSS trial cache memory after both optimization and validation for that DSS are complete (or skipped).
- **FR-013**: System MUST support standalone validation submissions (for previously completed optimizations) through the same task queue, ensuring the single-compute-task guarantee applies to all validation work.
- **FR-014**: System MUST preserve the existing optimization and validation tab behavior for displaying in-progress run details. The task queue panel is an additional view, not a replacement.

### Key Entities

- **Task Queue**: A singleton, in-memory ordered collection of compute tasks. Each task has a type (optimization or validation), a DSS index, a status (pending, in-progress, completed, cancelled, failed), and a reference to its parent job.
- **Job**: A top-level submission that groups all tasks for one optimization request. Contains strategy, parameter axes, DSS list, optimization method, validation settings, and max threads.
- **Per-DSS Trial Cache**: An in-memory cache holding the optimization trial results for a single DSS, passed directly to the validation phase for the same DSS to avoid database round-trips.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Only one compute-heavy task (optimization or validation) executes at any given time, regardless of how many jobs are submitted.
- **SC-002**: Validation for a DSS completes without any database reads for trial data when the optimization for the same DSS completed in the same job.
- **SC-003**: Users can see the current queue state (pending, in-progress, completed tasks) within 2 seconds of any status change.
- **SC-004**: Cancelling a pending task removes it from the queue within 1 second. Cancelling an in-progress task stops the computation within the timeout window of the current trial.
- **SC-005**: Trial data for each DSS is persisted to the database before or during the validation phase, ensuring no data loss if the application crashes during validation.
- **SC-006**: The system correctly handles application restart: in-progress tasks are marked as failed, completed results are preserved, and the queue resumes accepting new submissions.

## Clarifications

### Session 2026-04-14

- Q: Should standalone validation (for a previously completed optimization) also go through the task queue? → A: Yes, all validation goes through the queue, including standalone submissions for past optimizations. Single execution path ensures the "one compute task at a time" guarantee universally.
- Q: Should the queue UI show intra-task progress (combination counts, validation stages) or only task-level status? → A: Queue UI shows intra-task progress — combination counts for optimization, stage counts for validation — alongside task status.
- Q: What happens to completed tasks in the queue panel? → A: Completed tasks immediately leave the queue; only pending and in-progress tasks are shown. Completed results are viewable through the existing optimization/validation results pages.
- Q: How does the task queue panel relate to existing optimization/validation tabs? → A: The existing optimization/validation tabs continue to display in-progress run details as they do today. The task queue panel is an additional view that provides queue-level transparency (pending/in-progress tasks, cancel, purge). Both views coexist.

## Assumptions

- The ephemeral queue is in-memory only and does not survive application restarts. This is acceptable because optimization/validation are idempotent operations that can be resubmitted.
- The "single compute task at a time" constraint applies system-wide, not per-user. This simplifies resource management and avoids CPU contention.
- The existing trial collection and filtering mechanisms continue to be used within each optimization task. The queue pattern wraps around the existing execution logic, not replacing it.
- The genetic optimization method does not currently support multi-DSS groups. This limitation is preserved; genetic submissions enqueue a single optimization task (and optionally a single validation task).
- The max threads setting applies to the parallelism within a single compute task (worker threads), not to the queue itself (which is always serial).
