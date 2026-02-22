// T011 - JSONL event parser with message discriminator

import type { BacktestEvent } from "./types";
import type { ServerMessage, DebugSnapshot } from "@/types/api";

// ---------------------------------------------------------------------------
// Parsed message discriminated union
// ---------------------------------------------------------------------------

export type ParsedMessage =
  | { kind: "snapshot"; data: DebugSnapshot }
  | { kind: "error"; message: string }
  | { kind: "ack"; mutations: boolean }
  | { kind: "event"; data: BacktestEvent };

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

/**
 * Parses a raw JSON string into a `ParsedMessage`.
 *
 * Discrimination rules:
 * - If the parsed object has a `type` field, it is a `ServerMessage`:
 *   - `"snapshot"` -> `kind: "snapshot"`
 *   - `"error"` -> `kind: "error"`
 *   - `"set_export_ack"` -> `kind: "ack"`
 * - If the parsed object has a `_t` field, it is a `BacktestEvent` -> `kind: "event"`
 * - Otherwise, throw an error.
 */
export function parseMessage(raw: string): ParsedMessage {
  const parsed: unknown = JSON.parse(raw);

  if (typeof parsed !== "object" || parsed === null) {
    throw new Error(`Expected JSON object, got: ${typeof parsed}`);
  }

  const obj = parsed as Record<string, unknown>;

  // ServerMessage discrimination via `type` field
  if ("type" in obj && typeof obj.type === "string") {
    const msg = obj as unknown as ServerMessage;

    switch (msg.type) {
      case "snapshot":
        return { kind: "snapshot", data: msg as DebugSnapshot };

      case "error": {
        const errorMsg = msg as { type: "error"; message: string };
        return { kind: "error", message: errorMsg.message };
      }

      case "set_export_ack": {
        const ackMsg = msg as {
          type: "set_export_ack";
          mutations: boolean;
        };
        return { kind: "ack", mutations: ackMsg.mutations };
      }

      default:
        throw new Error(`Unknown server message type: ${(obj as Record<string, unknown>).type}`);
    }
  }

  // BacktestEvent discrimination via `_t` field
  if ("_t" in obj) {
    return { kind: "event", data: obj as unknown as BacktestEvent };
  }

  throw new Error(
    `Cannot discriminate message: no 'type' or '_t' field found`,
  );
}
