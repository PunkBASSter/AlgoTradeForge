// T009 - Structured logging utility

type LogLevel = "debug" | "info" | "warn" | "error";

interface Logger {
  debug: (message: string, data?: Record<string, unknown>) => void;
  info: (message: string, data?: Record<string, unknown>) => void;
  warn: (message: string, data?: Record<string, unknown>) => void;
  error: (message: string, data?: Record<string, unknown>) => void;
}

function log(
  level: LogLevel,
  component: string,
  message: string,
  data?: Record<string, unknown>,
): void {
  const prefix = `[${component}]`;

  switch (level) {
    case "debug":
      if (data) {
        console.debug(prefix, message, data);
      } else {
        console.debug(prefix, message);
      }
      break;
    case "info":
      if (data) {
        console.info(prefix, message, data);
      } else {
        console.info(prefix, message);
      }
      break;
    case "warn":
      if (data) {
        console.warn(prefix, message, data);
      } else {
        console.warn(prefix, message);
      }
      break;
    case "error":
      if (data) {
        console.error(prefix, message, data);
      } else {
        console.error(prefix, message);
      }
      break;
  }
}

export function createLogger(component: string): Logger {
  return {
    debug: (message: string, data?: Record<string, unknown>) =>
      log("debug", component, message, data),
    info: (message: string, data?: Record<string, unknown>) =>
      log("info", component, message, data),
    warn: (message: string, data?: Record<string, unknown>) =>
      log("warn", component, message, data),
    error: (message: string, data?: Record<string, unknown>) =>
      log("error", component, message, data),
  };
}
