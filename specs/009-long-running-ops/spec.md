# Feature Specification: Long-Running Operations Flow

**Feature Branch**: `009-long-running-ops`
**Created**: 2026-02-22
**Status**: Draft
**Input**: Redesign backtest and optimization command handlers from synchronous to asynchronous background execution with progress tracking and polling.

## Clarifications

### Session 2026-02-22

- Q: What happens when individual optimization trials fail while others succeed? → A: Skip failed trials and continue processing. Save all trials (including failed ones) with error info and stacktrace stored in the backtest/trial model. Display errors on the frontend.
- Q: Should the status poll endpoint include all completed trial data on every poll or just progress counts? → A: Status poll always returns progress counts and status. The response includes a nullable result field that is only populated when the run completes. One endpoint — no separate call needed for results.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Submit Backtest and Track Progress (Priority: P1)

A trader submits a backtest request and immediately receives a run identifier with preliminary details (such as the total number of data bars to process). The system processes the backtest in the background. The trader can check progress at any time and sees how many bars have been processed out of the total. Once the backtest completes, the trader retrieves the full results (metrics, equity curve, trades) just as they do today.

**Why this priority**: This is the core flow — without immediate response and background processing, long backtests cause HTTP timeouts and block the frontend. This story delivers the fundamental async pattern that everything else builds on.

**Independent Test**: Can be fully tested by submitting a backtest request, verifying immediate return of a run ID, polling the status endpoint until completion, and confirming that the final results match what the current synchronous handler produces.

**Acceptance Scenarios**:

1. **Given** a valid backtest request, **When** the user submits it, **Then** the system returns a run identifier and the total number of bars to process within 2 seconds, and the backtest begins processing in the background.
2. **Given** a backtest is processing in the background, **When** the user checks the status, **Then** the system returns the current number of processed bars, the total bar count, and a status indicator (e.g., running, completed, failed).
3. **Given** a backtest has completed, **When** the user checks the status, **Then** the system returns the full backtest results including all metrics, equivalent to what the current synchronous endpoint returns.
4. **Given** a backtest has failed during processing, **When** the user checks the status, **Then** the system returns a failed status with an error message describing the failure reason.

---

### User Story 2 - Submit Optimization and Track Progress (Priority: P1)

A trader submits an optimization request and immediately receives a run identifier with the total number of parameter combinations to evaluate. The system processes optimization trials in the background. The trader can check progress to see how many combinations have been evaluated (and how many failed) so far. When the optimization completes, the full results — including all trial records (successful and failed) — become available in the same status response.

**Why this priority**: Optimizations can run for hours with thousands of combinations. Without async processing, they consistently exceed HTTP timeouts and are unusable in production. Progress count visibility lets traders confirm processing is advancing, and full results are available immediately upon completion without a separate retrieval step.

**Independent Test**: Can be fully tested by submitting an optimization request, verifying immediate return of a run ID and total combinations count, polling the status endpoint to observe progress counts increasing, and confirming that the final poll with completed status includes full results matching the current synchronous output.

**Acceptance Scenarios**:

1. **Given** a valid optimization request, **When** the user submits it, **Then** the system returns a run identifier and the total number of parameter combinations within 2 seconds, and optimization begins processing in the background.
2. **Given** an optimization is processing, **When** the user checks the status, **Then** the system returns the number of completed combinations (and failed count) out of the total, a status indicator, and a null result field (results are only included upon completion).
3. **Given** an optimization has fully completed, **When** the user checks the status, **Then** the system returns the complete optimization results with all trials sorted by the specified metric, equivalent to the current synchronous response.
4. **Given** an optimization has failed entirely, **When** the user checks the status, **Then** the system returns a failed status with an error message.
5. **Given** individual trials fail during an optimization, **When** the user checks the status, **Then** the system continues processing remaining trials, reports the count of failed trials alongside successful ones, and includes error details for each failed trial.

---

### User Story 3 - Frontend Polls and Displays Progress (Priority: P2)

The frontend automatically polls for progress after the user submits a backtest or optimization. A progress indicator shows "Processed X / Total Bars" for backtests or "Processed X / Total Combinations" for optimizations. When the operation completes, the frontend displays the full results. The user does not need to manually refresh.

**Why this priority**: Provides the user-facing experience that makes the async flow usable. Without progress display, users have no visibility into whether their operation is still running or has failed.

**Independent Test**: Can be tested by submitting a run from the frontend, observing the progress indicator updating automatically, and verifying results display upon completion.

**Acceptance Scenarios**:

1. **Given** the user submits a backtest from the frontend, **When** the submission succeeds, **Then** the frontend begins polling every 5 seconds and displays "Processed X / Total Bars" with updating values.
2. **Given** the user submits an optimization from the frontend, **When** the submission succeeds, **Then** the frontend begins polling every 30 seconds and displays "Processed X / Total Combinations" with updating values.
3. **Given** a run completes while the frontend is polling, **When** the next poll returns completed status, **Then** the frontend stops polling and displays the full results.
4. **Given** a run fails while the frontend is polling, **When** the next poll returns failed status, **Then** the frontend stops polling and displays the error message.

---

### User Story 4 - Cancel a Running Operation (Priority: P3)

A trader who has submitted a backtest or optimization can cancel it before it completes. The system stops processing promptly and marks the run as cancelled.

**Why this priority**: Important for usability — traders may realize they submitted incorrect parameters or want to free up system resources. However, not required for the core async flow to function.

**Independent Test**: Can be tested by submitting a long-running operation, sending a cancel request, verifying the status changes to cancelled, and confirming that processing actually stops.

**Acceptance Scenarios**:

1. **Given** a backtest or optimization is currently processing, **When** the user requests cancellation using the run identifier, **Then** the system stops processing within 10 seconds and marks the run as cancelled.
2. **Given** a run has already completed, **When** the user attempts to cancel it, **Then** the system returns an indication that the run is already complete and cannot be cancelled.

---

### Edge Cases

- What happens when the server restarts while a backtest or optimization is running? The in-progress run is lost since in-memory state is volatile; the run should be considered failed. No automatic retry is attempted.
- What happens when two backtests or optimizations are submitted simultaneously? The system handles concurrent background operations independently without interference.
- What happens when the user polls for a run identifier that does not exist? The system returns a clear "not found" response.
- What happens when an optimization has zero valid parameter combinations? The system fails immediately at submission time with a validation error, before starting any background operation.
- What happens when the data period specified has no available candle data? The operation fails promptly with an appropriate error rather than running with no data.
- What happens when the user submits a new run while a previous one is still processing? Both run independently; there is no limit on concurrent runs (single-user local system).
- What happens when individual optimization trials fail while others succeed? The optimization continues processing remaining trials. Failed trials are saved alongside successful ones, with error information and stacktrace stored in the trial record. The frontend displays error details for failed trials.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST return a run identifier and summary details (total bars for backtest, total combinations for optimization) within 2 seconds of receiving a valid run request.
- **FR-002**: System MUST process backtests and optimizations in the background without blocking the request that initiated them.
- **FR-003**: System MUST expose a single status-check endpoint for each run type that returns the current progress (processed count vs. total count) and the run status. The response MUST include a nullable result field that is only populated when the run reaches a terminal state (completed, failed, or cancelled). No separate endpoint is needed to retrieve results.
- **FR-004**: System MUST track progress during backtest execution as the number of bars processed out of the total.
- **FR-005**: System MUST track progress during optimization execution as the number of parameter combinations evaluated out of the total.
- **FR-006**: During optimization, the status poll response MUST include only progress counts (completed and failed trial counts vs. total). The full trial results are included in the nullable result field only upon completion — they are NOT returned incrementally on each poll.
- **FR-007**: System MUST persist completed run results to durable storage, consistent with the existing persistence behavior.
- **FR-008**: System MUST store in-progress run state in a volatile in-memory store so that progress can be queried by run identifier.
- **FR-009**: System MUST support cancellation of in-progress backtests and optimizations by run identifier, stopping processing promptly.
- **FR-010**: System MUST mark in-progress runs as failed if the operation encounters an unhandled error. On server restart, volatile in-memory state is naturally lost.
- **FR-011**: The frontend MUST poll the backtest status endpoint every 5 seconds after submission.
- **FR-012**: The frontend MUST poll the optimization status endpoint every 30 seconds after submission.
- **FR-013**: The frontend MUST display progress as "Processed X / Total Bars" for backtests and "Processed X / Total Combinations" for optimizations.
- **FR-014**: The frontend MUST stop polling and display full results when a run reaches a terminal state (completed, failed, or cancelled).
- **FR-015**: System MUST validate the run request (parameter combinations count, data availability) before starting the background operation, returning validation errors synchronously.
- **FR-016**: System MUST store error information and stacktrace (when available) on each failed backtest or trial record, rather than discarding the record.
- **FR-017**: During optimization, the system MUST continue processing remaining trials when individual trials fail, saving both successful and failed trial records.
- **FR-018**: The frontend MUST display error details (message and stacktrace) for failed backtest runs and failed optimization trials.

### Key Entities

- **Run Status**: Represents the lifecycle state of a background operation — pending, running, completed, failed, or cancelled.
- **Backtest Progress**: Tracks a backtest's progress including the run identifier, total bars, processed bars, current status, error information with stacktrace (if failed), and — upon completion — the full backtest results.
- **Optimization Progress**: Tracks an optimization's progress including the run identifier, total combinations, completed combinations, failed trial count, current status, and a nullable result field containing the full sorted trial results (both successful and failed with error details) populated only upon completion.

## Assumptions

- This is a single-user, single-node local system. No distributed coordination or multi-instance considerations are needed.
- Polling is the communication model for progress updates. A push-based model (e.g., WebSockets, Server-Sent Events) may be considered in the future but is out of scope for this feature.
- The existing persistence layer (durable storage for completed runs) remains unchanged; only the in-progress tracking is new.
- The existing run list and detail-retrieval endpoints continue to work as before for completed runs.
- There is no limit on concurrent background operations since this is a local single-user system.
- In-memory progress data does not need to survive server restarts; only completed results are persisted.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users receive a run identifier within 2 seconds of submitting a backtest or optimization request, regardless of how long the actual computation takes.
- **SC-002**: Backtests that previously caused HTTP timeouts (over 30 seconds of processing) now complete successfully via the background processing flow.
- **SC-003**: Optimizations with thousands of parameter combinations complete successfully in the background, with progress visible to the user throughout.
- **SC-004**: Users can see progress updating in the frontend without manual page refreshes.
- **SC-005**: Cancelled operations stop processing within 10 seconds of the cancellation request.
- **SC-006**: All completed runs are persisted identically to how the current synchronous flow persists them — no data loss in the transition.
- **SC-007**: The frontend displays accurate progress counts that match actual processing state within one polling interval of the real progress.
