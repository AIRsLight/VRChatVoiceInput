export {};

declare global {
  interface Window {
    chrome: {
      webview: {
        postMessage(message: unknown): void;
        addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
      };
    };
  }
}
