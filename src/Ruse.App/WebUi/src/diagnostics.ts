export const DIAGNOSTICS_NODE_ID = 'ruse-diagnostics';
export const DIAGNOSTICS_LAYOUT_KEY = 'ruse.diagnosticsLayout';

export interface DiagnosticsLayout {
  x: number;
  y: number;
  scale: number;
}

export const DEFAULT_DIAGNOSTICS_LAYOUT: DiagnosticsLayout = {
  x: 900,
  y: 80,
  scale: 1,
};

export function loadDiagnosticsLayout(): DiagnosticsLayout {
  try {
    const raw = localStorage.getItem(DIAGNOSTICS_LAYOUT_KEY);
    if (!raw) return { ...DEFAULT_DIAGNOSTICS_LAYOUT };
    const value = JSON.parse(raw) as Partial<DiagnosticsLayout>;
    return {
      x: typeof value.x === 'number' ? value.x : DEFAULT_DIAGNOSTICS_LAYOUT.x,
      y: typeof value.y === 'number' ? value.y : DEFAULT_DIAGNOSTICS_LAYOUT.y,
      scale: Math.min(2, Math.max(0.6, value.scale ?? 1)),
    };
  } catch {
    return { ...DEFAULT_DIAGNOSTICS_LAYOUT };
  }
}

export function saveDiagnosticsLayout(layout: DiagnosticsLayout): void {
  try {
    localStorage.setItem(DIAGNOSTICS_LAYOUT_KEY, JSON.stringify(layout));
  } catch {
    // Storage can be full or disabled; the panel still works for this session.
  }
}
