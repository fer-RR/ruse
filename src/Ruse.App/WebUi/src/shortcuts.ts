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
  diagnostics: 'Ctrl+Shift+D',
  settings: 'Ctrl+,',
  transport: 'Space',
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
