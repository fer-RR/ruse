export const SHORTCUTS = {
  undo: 'Ctrl+Z',
  redo: 'Ctrl+Y',
  redoAlt: 'Ctrl+Shift+Z',
  duplicate: 'Ctrl+D',
  search: 'Ctrl+F',
  library: 'Ctrl+L',
  save: 'Ctrl+S',
  load: 'Ctrl+O',
  mixer: 'Ctrl+M',
  timeline: 'Ctrl+T',
  diagnostics: 'Ctrl+Shift+D',
  settings: 'Ctrl+,',
  transport: 'Ctrl+R',
  timelinePlay: 'Space',
  muteSelected: 'M',
  muteMics: 'Ctrl+Shift+M',
  bypass: 'B',
  remove: 'Del',
  removeAlt: 'Backspace',
  tidy: 'T',
  fit: 'F',
  resetZoom: '0',
  zoomIn: '+',
  zoomOut: '-',
  refresh: 'F5',
  escape: 'Escape',
  librarySections: 'Ctrl+1-5',
  help: 'F1 / ?',
} as const;

export type ShortcutAction = keyof typeof SHORTCUTS;

export function withShortcut(label: string, action: ShortcutAction): string {
  return `${label} (${SHORTCUTS[action]})`;
}

const INTERACTIVE_SELECTOR =
  'button, input, select, textarea, summary, a[href], ' +
  '[role="menuitem"], [role="slider"], [role="checkbox"], [role="radio"], ' +
  '[role="button"], [role="switch"], [role="tab"], [role="option"]';

/** True when the keyboard focus is on a control that owns its own keys. */
export function isInteractiveTarget(target: unknown): boolean {
  if (!target || typeof target !== 'object') return false;
  const element = target as {
    isContentEditable?: boolean;
    closest?: (selector: string) => unknown;
    tagName?: string;
  };
  if (element.isContentEditable) return true;
  if (typeof element.closest === 'function') {
    return element.closest(INTERACTIVE_SELECTOR) != null;
  }
  return ['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA'].includes((element.tagName ?? '').toUpperCase());
}

export function hasOpenDialog(): boolean {
  return typeof document !== 'undefined' && document.querySelector('dialog[open]') != null;
}

/** Global shortcuts must yield to modals and to focused controls. */
export function shouldIgnoreShortcut(
  event: { defaultPrevented?: boolean; ctrlKey?: boolean; metaKey?: boolean; target?: unknown },
  modalOpen: boolean,
): boolean {
  if (event.defaultPrevented || modalOpen || hasOpenDialog()) return true;
  return !(event.ctrlKey || event.metaKey) && isInteractiveTarget(event.target);
}
