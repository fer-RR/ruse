export type MessageHandler = (type: string, payload: unknown) => void;

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', handler: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', handler: (event: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: {
      webview?: WebViewBridge;
    };
  }
}

export const isHosted = Boolean(window.chrome?.webview);

export function send(type: string, payload: Record<string, unknown> = {}): void {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage({ type, payload });
  }
}

export function subscribe(handler: MessageHandler): () => void {
  const listener = (event: { data: unknown }) => {
    if (!event.data || typeof event.data !== 'object') {
      return;
    }

    const message = event.data as { type?: unknown; payload?: unknown };
    if (typeof message.type === 'string') {
      handler(message.type, message.payload);
    }
  };

  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('message', listener);
    return () => window.chrome?.webview?.removeEventListener('message', listener);
  }

  return () => undefined;
}
