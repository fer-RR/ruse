import { memo, useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import type { DragEvent, ReactNode, RefObject } from 'react';
import {
  AppWindow, AudioLines, Check, ChevronDown, Combine, Eye, EyeOff, FileAudio, Folder, FolderOpen, GripVertical, Headphones, MessageSquare,
  Layers3, Mic, Network, Pencil, Plus, Puzzle, Radio, RefreshCw, Search, SlidersHorizontal, Speaker, Timer, Trash2, Upload, X,
} from 'lucide-react';
import {
  DndContext, KeyboardSensor, PointerSensor, closestCenter, useSensor, useSensors,
} from '@dnd-kit/core';
import type { DragEndEvent } from '@dnd-kit/core';
import {
  SortableContext, arrayMove, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useI18n } from '../i18n';
import { useReducedMotion } from '../useReducedMotion';
import { catalogKey } from '../library';
import { send } from '../bridge';
import { useTitleActive } from '../telemetry';
import type { CatalogItem, CatalogSnapshot, MediaListModel } from '../types';
import { ConfirmDialog } from './Dialogs';
const systemItem: CatalogItem = {
  id: 'system', kind: 'system', name: 'System audio', subtitle: 'Windows loopback',
  details: 'Capture everything playing through the default output',
};

const toneItem: CatalogItem = {
  id: 'tone', kind: 'tone', name: 'Test tone', subtitle: 'Sine, square, saw, noise',
  details: 'Generate a test signal for routing and level checks',
};

const metronomeItem: CatalogItem = {
  id: 'metronome', kind: 'metronome', name: 'Metronome', subtitle: 'Tempo and beat click',
  details: 'Generate a click track at a chosen tempo and beats per bar',
};

const synthItem: CatalogItem = {
  id: 'synth', kind: 'synth', name: 'Sound generator', subtitle: 'Procedural synth',
  details: 'Shape a synthesized tone with pitch, timbre, harmonics, filter and movement controls',
};

const ttsItem: CatalogItem = {
  id: 'tts', kind: 'tts', name: 'Text to speech', subtitle: 'Local TTS',
  details: 'Speak typed text, a text file, or the output of a transcriber or translator',
};
const networkInputItem: CatalogItem = {
  id: 'network-in', kind: 'network-in', name: 'Network input', subtitle: 'UDP PCM receiver',
  details: 'Receive a Ruse network stream on this PC',
};

const networkOutputItem: CatalogItem = {
  id: 'network-out', kind: 'network-out', name: 'Network output', subtitle: 'UDP PCM sender',
  details: 'Send this route to another Ruse instance',
};
const recorderItem: CatalogItem = {
  id: 'recorder', kind: 'recorder', name: 'Record to file', subtitle: 'WAV, MP3 or FLAC',
  details: 'Add a file destination for a routed mix',
};

const filePlayerItem: CatalogItem = {
  id: 'file-player', kind: 'file-player', name: 'File player', subtitle: 'WAV, MP3, FLAC',
  details: 'Play an audio file into a route',
};

const ItemIcon = memo(function ItemIcon({ item }: { item: CatalogItem }) {
  const props = { size: 15, strokeWidth: 1.7, 'aria-hidden': true } as const;
  if (item.iconDataUrl) return <img className="library-app-icon" src={item.iconDataUrl} alt="" />;
  switch (item.kind) {
    case 'app': return <AppWindow {...props} />;
    case 'system': return <AudioLines {...props} />;
    case 'metronome': return <Timer {...props} />;
    case 'tone': return <AudioLines {...props} />;
    case 'synth': return <SlidersHorizontal {...props} />;
    case 'tts': return <MessageSquare {...props} />;
    case 'network-in': return <Network {...props} />;
    case 'network-out': return <Network {...props} />;
    case 'input': return item.virtual ? <Radio {...props} /> : <Mic {...props} />;
    case 'output': {
      const name = item.name.toLowerCase();
      return name.includes('headphone') || name.includes('headset') || name.includes('fone')
        ? <Headphones {...props} />
        : <Speaker {...props} />;
    }
    case 'virtual': return <Radio {...props} />;
    case 'effect': return <SlidersHorizontal {...props} />;
    case 'mixer': return <Combine {...props} />;
    case 'chain': return <Layers3 {...props} />;
    case 'vst3': return <Puzzle {...props} />;
    case 'file-player': return <FileAudio {...props} />;
    case 'sample': return <FileAudio {...props} />;
    case 'recorder': return <FileAudio {...props} />;
  }
});

const LibraryItem = memo(function LibraryItem({ item, name, onAdd, onEdit, onRename, onDelete, onHide, onCanvas, hidden = false }: {
  item: CatalogItem;
  name: string;
  onAdd: (item: CatalogItem) => void;
  onEdit?: (item: CatalogItem) => void;
  onRename?: (item: CatalogItem) => void;
  onDelete?: (item: CatalogItem) => void;
  onHide?: (item: CatalogItem) => void;
  onCanvas?: boolean;
  hidden?: boolean;
}) {
  const { t } = useI18n();
  const active = useTitleActive(name) || item.active === true;
  return (
    <div
      className={`library-item item-${item.kind} ${item.kind === 'input' && item.virtual ? 'item-virtual-bus' : ''} ${active ? 'flowing' : ''} ${hidden ? 'library-item-hidden' : ''}`}
      role="button"
      tabIndex={onCanvas ? -1 : 0}
      aria-disabled={onCanvas || undefined}
      aria-label={`${name}. ${t(item.subtitle)}`}
      title={onCanvas
        ? t('Already on the canvas')
        : `${t(item.details)}. ${t('Drag onto the canvas or double-click to add.')}`}
      draggable={!onCanvas}
      onDragStart={event => {
        if (onCanvas) {
          event.preventDefault();
          return;
        }

        event.dataTransfer.setData('application/ruse-item', JSON.stringify(item));
        event.dataTransfer.effectAllowed = 'copy';
      }}
      onDoubleClick={event => {
        if (onCanvas) return;
        if ((event.target as HTMLElement).closest('button')) return;
        onAdd(item);
      }}
      onKeyDown={event => {
        if (event.target !== event.currentTarget) return;
        if (!onCanvas && (event.key === 'Enter' || event.key === ' ')) {
          event.preventDefault();
          onAdd(item);
        }
      }}
    >
      <span className="library-icon"><ItemIcon item={item} /></span>
      <span className="library-copy">
        <span className="library-name">{name}</span>
        <span className="library-subtitle">
          {item.badge && <span className="item-badge">{item.badge}</span>}
          {hidden && <span className="item-badge hidden-badge">{t('Hidden')}</span>}
          {t(item.subtitle)}
        </span>
      </span>
      {onCanvas
        ? <span className="library-actions"><span className="library-add library-added" title={t('Already on the canvas')}><Check size={14} /></span>{onHide && <button className="library-add" title={hidden ? t('Unhide {name}', { name }) : t('Hide {name}', { name })} aria-label={hidden ? t('Unhide {name}', { name }) : t('Hide {name}', { name })} onClick={event => { event.stopPropagation(); onHide(item); }}>{hidden ? <EyeOff size={13} /> : <Eye size={13} />}</button>}</span>
        : <span className="library-actions">
            {onEdit && <button className="library-add" title={t('Edit {name}', { name })} aria-label={t('Edit {name}', { name })} onClick={event => { event.stopPropagation(); onEdit(item); }}>
              <Layers3 size={13} />
            </button>}
            {onRename && <button className="library-add" title={t('Rename {name}', { name })} aria-label={t('Rename {name}', { name })} onClick={event => { event.stopPropagation(); onRename(item); }}>
              <Pencil size={13} />
            </button>}
            {onDelete && <button className="library-add library-delete" title={t('Delete {name}', { name })} aria-label={t('Delete {name}', { name })} onClick={event => { event.stopPropagation(); onDelete(item); }}>
              <Trash2 size={13} />
            </button>}
            {onHide && <button className="library-add" title={hidden ? t('Unhide {name}', { name }) : t('Hide {name}', { name })} aria-label={hidden ? t('Unhide {name}', { name }) : t('Hide {name}', { name })} onClick={event => { event.stopPropagation(); onHide(item); }}>{hidden ? <EyeOff size={13} /> : <Eye size={13} />}</button>}
            <button className="library-add" title={t('Add {name}', { name })} aria-label={t('Add {name}', { name })} onClick={event => { event.stopPropagation(); onAdd(item); }}>
              <Plus size={14} />
            </button>
          </span>}
    </div>
  );
});

interface Section {
  id: string;
  label: string;
  items: CatalogItem[];
  total?: number;
  hiddenCount?: number;
}

const LIBRARY_ORDER_KEY = 'ruse.librarySectionOrder.v3';
const LIBRARY_HIDDEN_KEY = 'ruse.libraryHidden.v1';

function loadHiddenItems(): Set<string> {
  try {
    const raw = localStorage.getItem(LIBRARY_HIDDEN_KEY);
    const parsed = raw ? JSON.parse(raw) as unknown : null;
    if (Array.isArray(parsed)) {
      return new Set(parsed.filter((value): value is string => typeof value === 'string'));
    }
  } catch {
    // Preferences are optional; fall back to showing everything.
  }

  return new Set();
}
const DEFAULT_SECTION_ORDER = [
  'applications', 'generators', 'samples', 'media', 'network', 'physicalOutputs', 'physicalInputs',
  'applications', 'generators', 'media', 'network', 'physicalOutputs', 'physicalInputs',
  'plugins', 'chains', 'templates',
] as const;

function loadSectionOrder(): string[] {
  try {
    const raw = localStorage.getItem(LIBRARY_ORDER_KEY);
    const parsed = raw ? JSON.parse(raw) as unknown : null;
    if (Array.isArray(parsed)) {
      const known = new Set<string>(DEFAULT_SECTION_ORDER);
      const order = [...new Set(parsed.filter((value): value is string =>
        typeof value === 'string' && known.has(value)))];
      for (let index = 0; index < DEFAULT_SECTION_ORDER.length; index++) {
        const id = DEFAULT_SECTION_ORDER[index];
        if (order.includes(id)) continue;
        let insertAt = order.length;
        for (let previous = index - 1; previous >= 0; previous--) {
          const previousAt = order.indexOf(DEFAULT_SECTION_ORDER[previous]);
          if (previousAt >= 0) {
            insertAt = previousAt + 1;
            break;
          }
        }

        order.splice(insertAt, 0, id);
      }

      return order;
    }
  } catch {
    // Preferences are optional; fall back to the default order.
  }

  return [...DEFAULT_SECTION_ORDER];
}

function SortableSection({ section, collapsed, reorderEnabled, hiddenRevealed, onToggle, onToggleHidden, children }: {
  section: Section;
  collapsed: boolean;
  reorderEnabled: boolean;
  hiddenRevealed: boolean;
  onToggle: () => void;
  onToggleHidden: () => void;
  children: ReactNode;
}) {
  const { t } = useI18n();
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: section.id,
    disabled: !reorderEnabled,
  });
  const style = { transform: CSS.Transform.toString(transform), transition };

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={`library-section ${isDragging ? 'dragging' : ''}`}
      data-section-label={section.label}
    >
      <div className="section-header">
        <button
          type="button"
          className="section-drag"
          {...attributes}
          {...listeners}
          disabled={!reorderEnabled}
          title={t('Drag to reorder')}
          aria-label={t('Reorder {name}', { name: t(section.label) })}
        ><GripVertical size={13} /></button>
        <button type="button" className="section-toggle" onClick={onToggle} aria-expanded={!collapsed}>
          <span>{t(section.label)}</span><span className="section-count">{section.total ?? section.items.length}</span>{section.hiddenCount ? <span className="section-hidden-count" title={t('{count} hidden', { count: section.hiddenCount })}>{t('{count} hidden', { count: section.hiddenCount })}</span> : null}
        </button>
        {section.hiddenCount ? <button type="button" className={`section-hidden-toggle ${hiddenRevealed ? 'active' : ''}`} title={hiddenRevealed ? t('Hide hidden items') : t('Show hidden items')} aria-label={hiddenRevealed ? t('Hide hidden items') : t('Show hidden items')} aria-pressed={hiddenRevealed} onClick={onToggleHidden}>{hiddenRevealed ? <Eye size={12} /> : <EyeOff size={12} />}</button> : null}
        <button type="button" className="section-expand" onClick={onToggle} aria-expanded={!collapsed} title={collapsed ? t('Expand {name}', { name: t(section.label) }) : t('Collapse {name}', { name: t(section.label) })} aria-label={collapsed ? t('Expand {name}', { name: t(section.label) }) : t('Collapse {name}', { name: t(section.label) })}><ChevronDown size={13} className={collapsed ? 'collapsed' : ''} /></button>
      </div>
      {!collapsed && <div className="section-items">{children}</div>}
    </div>
  );
}

interface Group {
  title: string;
  sections: Section[];
}

type View = 'all' | 'sources' | 'destinations' | 'processing';

const VIEWS: ReadonlyArray<{ id: View; label: string }> = [
  { id: 'all', label: 'All' },
  { id: 'sources', label: 'Sources' },
  { id: 'destinations', label: 'Destinations' },
  { id: 'processing', label: 'Processing' },
];
function formatMediaSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${Math.round(bytes)} B`;
}

function mediaWaveformPath(peaks?: number[]): string {
  if (!peaks || peaks.length === 0) return '';
  const width = 64;
  const middle = 9;
  return peaks.map((peak, index) => {
    const x = ((index + 0.5) * width / peaks.length).toFixed(2);
    const half = Math.max(0.4, Math.min(1, peak) * 8);
    return `M${x} ${(middle - half).toFixed(2)}V${(middle + half).toFixed(2)}`;
  }).join(' ');
}

function MediaBin({ media, waveforms, query }: { media: MediaListModel; waveforms: Record<string, number[]>; query: string }) {
  const { t } = useI18n();
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>(() => {
    try {
      const parsed: unknown = JSON.parse(localStorage.getItem('ruse.mediaCollapsed') ?? '{}');
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
        ? Object.fromEntries(Object.entries(parsed as Record<string, unknown>).filter((entry): entry is [string, boolean] => typeof entry[1] === 'boolean'))
        : {};
    } catch {
      return {};
    }
  });
  const [renaming, setRenaming] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [draggingFolder, setDraggingFolder] = useState<string | null>(null);
  const [dropFolder, setDropFolder] = useState<string | null>(null);
  const [sort, setSort] = useState<'name' | 'modified' | 'size' | 'type'>('name');
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; file: MediaListModel['files'][number] } | null>(null);
  const [pendingRemoveFolder, setPendingRemoveFolder] = useState<MediaListModel['folders'][number] | null>(null);
  const renameCommitted = useRef(false);
  const requestedWaveforms = useRef<Set<string>>(new Set());
  const normalized = query.trim().toLowerCase();

  useEffect(() => {
    const missing: string[] = [];
    for (const folder of media.folders) {
      if (!normalized && collapsed[folder.path]) continue;
      for (const file of media.files) {
        if (file.folder !== folder.path) continue;
        if (waveforms[file.path] || requestedWaveforms.current.has(file.path)) continue;
        requestedWaveforms.current.add(file.path);
        missing.push(file.path);
        if (missing.length >= 8) break;
      }

      if (missing.length >= 8) break;
    }

    for (const path of missing) send('media-waveform', { path });
  }, [collapsed, media.files, media.folders, normalized, waveforms]);

  const fileMatches = (file: MediaListModel['files'][number]) =>
    !normalized ||
    file.name.toLowerCase().includes(normalized) ||
    file.extension.toLowerCase().includes(normalized);

  useEffect(() => { localStorage.setItem('ruse.mediaCollapsed', JSON.stringify(collapsed)); }, [collapsed]);

  const sortFiles = (files: MediaListModel['files']) => [...files].sort((a, b) => {
    if (sort === 'modified') return b.modifiedUtc.localeCompare(a.modifiedUtc);
    if (sort === 'size') return b.sizeBytes - a.sizeBytes;
    if (sort === 'type') return a.extension.localeCompare(b.extension) || a.name.localeCompare(b.name);
    return a.name.localeCompare(b.name);
  });
  useEffect(() => { const refresh = () => send('media-scan'); window.addEventListener('focus', refresh); return () => window.removeEventListener('focus', refresh); }, []);
  useEffect(() => {
    if (!contextMenu) return;
    const close = () => setContextMenu(null);
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') close(); };
    window.addEventListener('pointerdown', close);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('pointerdown', close);
      window.removeEventListener('keydown', onKey);
    };
  }, [contextMenu]);

  const startRename = (path: string, name: string) => {
    renameCommitted.current = false;
    setRenaming(path);
    setDraft(name);
  };

  const commitRename = () => {
    if (!renaming || renameCommitted.current) return;
    renameCommitted.current = true;
    const name = draft.trim();
    if (name && name !== media.files.find(file => file.path === renaming)?.name) {
      send('media-rename', { path: renaming, name });
    }
    setRenaming(null);
  };

  const reorderFolders = (from: string, to: string) => {
    if (!from || !to || from === to) return;
    const paths = media.folders.map(folder => folder.path);
    const fromIndex = paths.indexOf(from);
    const toIndex = paths.indexOf(to);
    if (fromIndex < 0 || toIndex < 0) return;
    const [moved] = paths.splice(fromIndex, 1);
    paths.splice(toIndex, 0, moved);
    send('media-reorder-folders', { paths });
  };

  return (
    <div className="media-bin">
      <div className="library-section-actions media-actions">
        <button type="button" className="library-action" onClick={() => send('samples-open-folder')} title={t('Open samples folder')}>
          <FolderOpen size={13} /> {t('Open folder')}
        </button>
        <button type="button" className="library-action" onClick={() => send('samples-import')} title={t('Add sample files')}>
          <Upload size={13} /> {t('Add samples')}
        </button>
        <button type="button" className="library-action" onClick={() => send('media-add-folder')}>
          <FolderOpen size={13} /> {t('Add folder')}
        </button>
        <select
          className="media-sort"
          value={sort}
          onChange={event => setSort(event.target.value as typeof sort)}
          aria-label={t('Sort media')}
          title={t('Sort media')}
        >
          <option value="name">{t('Name')}</option>
          <option value="modified">{t('Date')}</option>
          <option value="size">{t('Size')}</option>
          <option value="type">{t('Type')}</option>
        </select>
        <button type="button" className="library-action" onClick={() => send('media-scan')} title={t('Refresh')}>
          <RefreshCw size={13} /> {t('Refresh')}
        </button>
      </div>
      {media.error && <p className="library-empty error-text">{media.error}</p>}
      {media.folders.length === 0 && <p className="library-empty">{t('Add a project folder to collect the files you use here.')}</p>}
      <div className="media-groups">
        {media.folders.map(folder => {
          const files = sortFiles(media.files.filter(file => file.folder === folder.path && fileMatches(file)));
          if (normalized && files.length === 0) return null;
          const open = normalized.length > 0 || !collapsed[folder.path];
          return (
            <section
              key={folder.path}
              className={`media-group ${folder.exists ? '' : 'missing'} ${dropFolder === folder.path ? 'drop-target' : ''}`}
              onDragOver={event => {
                if (!draggingFolder) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                setDropFolder(folder.path);
              }}
              onDragLeave={() => setDropFolder(current => current === folder.path ? null : current)}
              onDrop={event => {
                event.preventDefault();
                const from = event.dataTransfer.getData('application/ruse-media-folder') || draggingFolder;
                setDropFolder(null);
                setDraggingFolder(null);
                if (from) reorderFolders(from, folder.path);
              }}
            >
              <div className="media-group-head">
                <span
                  className="media-folder-drag"
                  draggable
                  title={t('Drag to reorder folders')}
                  aria-label={t('Drag to reorder folders')}
                  onDragStart={event => {
                    setDraggingFolder(folder.path);
                    event.dataTransfer.setData('application/ruse-media-folder', folder.path);
                    event.dataTransfer.effectAllowed = 'move';
                  }}
                  onDragEnd={() => {
                    setDraggingFolder(null);
                    setDropFolder(null);
                  }}
                ><GripVertical size={12} /></span>
                <button
                  type="button"
                  className="media-group-toggle"
                  onClick={() => setCollapsed(current => ({ ...current, [folder.path]: !current[folder.path] }))}
                  aria-expanded={open}
                >
                  <ChevronDown size={12} className={open ? '' : 'collapsed'} />
                  <Folder size={13} />
                  <strong>{folder.name}</strong>
                  <span>{files.length}</span>
                </button>
                <button type="button" className="icon-button compact" title={t('Open folder')} aria-label={t('Open folder')} onClick={() => send('media-open-folder', { path: folder.path })}>
                  <FolderOpen size={12} />
                </button>
                <button type="button" className="icon-button compact danger-text" title={t('Remove folder')} aria-label={t('Remove folder')} onClick={() => setPendingRemoveFolder(folder)}>
                  <X size={12} />
                </button>
              </div>
              {open && (
                files.length > 0 ? (
                  <div className="media-group-items">
                    {files.map(file => (
                      <div
                        key={file.path}
                        className={`media-item ${renaming === file.path ? 'editing' : ''}`}
                        draggable={renaming !== file.path}
                        tabIndex={0}
                        onContextMenu={event => {
                          event.preventDefault();
                          setContextMenu({ x: event.clientX, y: event.clientY, file });
                        }}
                        onKeyDown={event => {
                          if (event.target !== event.currentTarget) return;
                          if (event.key === 'F2') {
                            event.preventDefault();
                            startRename(file.path, file.name);
                          } else if (event.key === 'Enter') {
                            event.preventDefault();
                            send('add-file-player', { path: file.path });
                          }
                        }}
                        onDragStart={event => {
                          if (renaming === file.path) {
                            event.preventDefault();
                            return;
                          }
                          event.dataTransfer.setData('application/ruse-recording', file.path);
                          event.dataTransfer.setData('application/ruse-media', file.path);
                          event.dataTransfer.effectAllowed = 'copy';
                        }}
                        onDoubleClick={() => send('add-file-player', { path: file.path })}
                        title={file.path + ' · ' + t('Drag to the timeline, double-click to add a file player')}
                      >
                        <FileAudio size={13} aria-hidden="true" />
                        <span className="media-item-text">
                          {renaming === file.path ? (
                            <input
                              autoFocus
                              value={draft}
                              onChange={event => setDraft(event.target.value)}
                              onBlur={commitRename}
                              onKeyDown={event => {
                                if (event.key === 'Enter') { event.preventDefault(); commitRename(); }
                                if (event.key === 'Escape') { event.preventDefault(); setRenaming(null); }
                              }}
                            />
                          ) : <strong>{file.name}</strong>}
                          <small>{file.extension} � {formatMediaSize(file.sizeBytes)} � {new Date(file.modifiedUtc).toLocaleDateString()}</small>
                        </span>
                        <svg className="media-waveform" viewBox="0 0 64 18" aria-hidden="true">
                          <path d={mediaWaveformPath(waveforms[file.path])} />
                        </svg>
                        <button type="button" className="icon-button compact" title={t('Rename')} aria-label={t('Rename')} onClick={() => startRename(file.path, file.name)}><Pencil size={12} /></button>
                      </div>
                    ))}
                  </div>
                ) : <p className="media-folder-empty">{t('No media files in these folders.')}</p>
              )}
            </section>
          );
        })}
      </div>
      {contextMenu && (
        <div
          className="media-context-menu"
          role="menu"
          style={{
            left: Math.min(contextMenu.x, window.innerWidth - 200),
            top: Math.min(contextMenu.y, window.innerHeight - 190),
          }}
          onPointerDown={event => event.stopPropagation()}
        >
          <button type="button" role="menuitem" onClick={() => { send('add-file-player', { path: contextMenu.file.path }); setContextMenu(null); }}>{t('Add to canvas')}</button>
          <button type="button" role="menuitem" onClick={() => { send('media-add-to-timeline', { path: contextMenu.file.path }); setContextMenu(null); }}>{t('Add to timeline')}</button>
          <button type="button" role="menuitem" onClick={() => { startRename(contextMenu.file.path, contextMenu.file.name); setContextMenu(null); }}>{t('Rename')}</button>
          <button type="button" role="menuitem" onClick={() => { send('media-reveal', { path: contextMenu.file.path }); setContextMenu(null); }}>{t('Reveal in Explorer')}</button>
          <button type="button" role="menuitem" onClick={() => { void navigator.clipboard?.writeText(contextMenu.file.path); setContextMenu(null); }}>{t('Copy path')}</button>
        </div>
      )}
      {pendingRemoveFolder && (
        <ConfirmDialog
          title={t('Remove folder')}
          body={t('Remove “{name}” from Media & Samples? The files on disk are not deleted.', { name: pendingRemoveFolder.name })}
          confirmLabel={t('Remove')}
          onConfirm={() => {
            send('media-remove-folder', { path: pendingRemoveFolder.path });
            setPendingRemoveFolder(null);
          }}
          onClose={() => setPendingRemoveFolder(null)}
        />
      )}
    </div>
  );
}

function Sidebar({ catalog, searchRef, aliases, usedDeviceIds, media, waveforms, onAdd, onRename, onDelete, onNewChain, onEditChain, onRescanPlugins }: {
  catalog: CatalogSnapshot;
  searchRef: RefObject<HTMLInputElement | null>;
  aliases: Record<string, string>;
  usedDeviceIds: Set<string>;
  media: MediaListModel;
  waveforms: Record<string, number[]>;
  onAdd: (item: CatalogItem) => void;
  onRename: (item: CatalogItem) => void;
  onDelete: (item: CatalogItem) => void;
  onNewChain: () => void;
  onEditChain: (item: CatalogItem) => void;
  onRescanPlugins: () => void;
}) {
  const { t } = useI18n();
  const reducedMotion = useReducedMotion();

  const [query, setQuery] = useState('');
  const [view, setView] = useState<View>('all');
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  const [searching, setSearching] = useState(false);
  const [sectionOrder, setSectionOrder] = useState<string[]>(loadSectionOrder);
  const [sampleDropActive, setSampleDropActive] = useState(false);
  const [hiddenKeys, setHiddenKeys] = useState<Set<string>>(loadHiddenItems);
  const [revealedSections, setRevealedSections] = useState<Set<string>>(new Set());
  const sectionRank = useMemo(
    () => new Map(sectionOrder.map((id, index) => [id, index])),
    [sectionOrder],
  );
  const ordered = useCallback((sections: Section[]) => [...sections].sort((a, b) =>
    (sectionRank.get(a.id) ?? Number.MAX_SAFE_INTEGER) -
    (sectionRank.get(b.id) ?? Number.MAX_SAFE_INTEGER)), [sectionRank]);
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  useEffect(() => {
    localStorage.setItem(LIBRARY_ORDER_KEY, JSON.stringify(sectionOrder));
  }, [sectionOrder]);
  useEffect(() => { send('media-scan'); }, []);

  useEffect(() => {
    localStorage.setItem(LIBRARY_HIDDEN_KEY, JSON.stringify([...hiddenKeys]));
  }, [hiddenKeys]);


  const toggleSectionHidden = useCallback((id: string) => {
    setRevealedSections(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const toggleHidden = useCallback((item: CatalogItem) => {
    const key = catalogKey(item);
    setHiddenKeys(current => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }, []);

  const openSearch = () => {
    setSearching(true);
    requestAnimationFrame(() => requestAnimationFrame(() => searchRef.current?.focus()));
  };

  const stopSearch = () => {
    setQuery('');
    setSearching(false);
  };

  useEffect(() => {
    const focusSearch = () => openSearch();
    window.addEventListener('ruse-focus-search', focusSearch);
    return () => window.removeEventListener('ruse-focus-search', focusSearch);
  }, []);

  const catalogSections = useMemo(() => {
    const physicalInputs = catalog.inputs.filter(item => !item.virtual);
    const virtualInputs = catalog.inputs.filter(item => item.virtual);
    const physicalOutputs = catalog.outputs;
    const hearing = catalog.effects.filter(item => item.id === 'hearing-profile');
    const effects = catalog.effects.filter(item => item.id !== 'hearing-profile');
    return {
      applications: { id: 'applications', label: 'Applications', items: [systemItem, ...catalog.apps] },
      network: { id: 'network', label: 'Network', items: [networkInputItem, networkOutputItem] },
      physicalOutputs: { id: 'physicalOutputs', label: 'Speakers & headphones', items: physicalOutputs },
      generators: { id: 'generators', label: 'Miscellaneous', items: [metronomeItem, toneItem, synthItem, ttsItem, ...hearing] },
      physicalInputs: { id: 'physicalInputs', label: 'Microphones & inputs', items: physicalInputs },
      recording: { id: 'recording', label: 'Record & Play', items: [recorderItem, filePlayerItem] },
      media: { id: 'media', label: 'Media & Samples', items: [] },
      virtualBuses: { id: 'virtualBuses', label: 'Virtual buses', items: virtualInputs },
      virtualInputs: { id: 'virtualInputs', label: 'Virtual inputs', items: catalog.virtuals },
      effects: { id: 'effects', label: 'Effects', items: effects },
      plugins: { id: 'plugins', label: 'VST3 plugins', items: catalog.vst3Plugins },
      chains: { id: 'chains', label: 'Saved chains', items: catalog.chains },
      templates: { id: 'templates', label: 'Frame templates', items: catalog.frameTemplates },
    };
  }, [catalog, t]);

  const groups = useMemo<Group[]>(() => [
    {
      title: 'Sources',
      sections: ordered([
        catalogSections.applications,
        catalogSections.generators,
        catalogSections.media,
        catalogSections.physicalInputs,
        catalogSections.virtualBuses,
      ]),
    },
    {
      title: 'Destinations',
      sections: ordered([
        catalogSections.physicalOutputs,
        catalogSections.recording,
        catalogSections.virtualInputs,
      ]),
    },
    {
      title: 'Processing',
      sections: ordered([catalogSections.effects, catalogSections.plugins, catalogSections.chains, catalogSections.templates]),
    },
  ], [catalogSections, ordered]);

  const allSections = useMemo(() => ordered([
    catalogSections.applications,
    catalogSections.generators,
    catalogSections.media,
    catalogSections.network,
    catalogSections.physicalOutputs,
    catalogSections.physicalInputs,
    catalogSections.recording,
    catalogSections.virtualBuses,
    catalogSections.virtualInputs,
    catalogSections.effects,
    catalogSections.plugins,
    catalogSections.chains,
    catalogSections.templates,
  ]).map(section => {
    const hiddenCount = section.items.filter(item => hiddenKeys.has(catalogKey(item))).length;
    const items = revealedSections.has(section.id)
      ? section.items
      : section.items.filter(item => !hiddenKeys.has(catalogKey(item)));
    return { ...section, items, total: section.id === 'media' ? media.files.length : section.items.length, hiddenCount };
  }).filter(section => section.id === 'media' || section.items.length > 0 || section.hiddenCount > 0 || section.label === 'Saved chains' || section.id === 'plugins'), [catalogSections, media.files.length, ordered, hiddenKeys, revealedSections]);

  useEffect(() => {
    const focusSection = (event: Event) => {
      const label = (event as CustomEvent<{ label?: string }>).detail?.label;
      if (!label) return;
      const target = groups.flatMap(group => group.sections.map(section => ({ group: group.title, ...section })))
        .find(section => section.label === label);
      if (!target) return;
      setQuery('');
      setView('all');
      setExpanded(current => ({ ...current, [target.id]: true }));
      requestAnimationFrame(() => requestAnimationFrame(() => {
        document.querySelector(`[data-section-label="${target.label}"]`)
          ?.scrollIntoView({ block: 'nearest', behavior: reducedMotion ? 'auto' : 'smooth' });
      }));
    };

    window.addEventListener('ruse-focus-section', focusSection);
    return () => window.removeEventListener('ruse-focus-section', focusSection);
  }, [groups, reducedMotion]);

  const displayName = (item: CatalogItem) => aliases[catalogKey(item)]?.trim() || t(item.name);
  const deferredQuery = useDeferredValue(query);
  const normalized = deferredQuery.trim().toLowerCase();
  const filtered = useMemo(() => groups.map(group => ({
    ...group,
    sections: group.sections.map(section => {
      const hiddenCount = section.items.filter(item => hiddenKeys.has(catalogKey(item))).length;
      const searched = normalized
        ? section.items.filter(item =>
            `${displayName(item)} ${t(item.name)} ${t(item.subtitle)} ${t(item.details)}`.toLowerCase().includes(normalized))
        : section.items;
      const items = revealedSections.has(section.id)
        ? searched
        : searched.filter(item => !hiddenKeys.has(catalogKey(item)));
      return { ...section, items, total: section.id === 'media' ? media.files.length : section.items.length, hiddenCount };
    }).filter(section => (section.id === 'media' && (!normalized || media.files.some(file => `${file.name} ${file.extension} ${file.folder}`.toLowerCase().includes(normalized)))) || section.items.length > 0 || (!normalized && (section.hiddenCount > 0 || section.label === 'Saved chains' || section.id === 'plugins'))),
  })), [aliases, groups, hiddenKeys, media.files.length, normalized, revealedSections, t]);

  const visible = useMemo(() => normalized
    ? filtered.filter(group => group.sections.length > 0)
    : view === 'all'
      ? [{ title: '', sections: allSections }]
      : filtered.filter(group =>
          group.sections.length > 0 &&
          ((view === 'sources' && group.title === 'Sources') ||
            (view === 'destinations' && group.title === 'Destinations') ||
            (view === 'processing' && group.title === 'Processing'))), [allSections, filtered, normalized, view]);

  const visibleSectionIds = useMemo(
    () => visible.flatMap(group => group.sections.map(section => section.id)),
    [visible],
  );

  const onSectionDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = visibleSectionIds.indexOf(String(active.id));
    const newIndex = visibleSectionIds.indexOf(String(over.id));
    if (oldIndex < 0 || newIndex < 0) return;

    const reordered = arrayMove(visibleSectionIds, oldIndex, newIndex);
    setSectionOrder(current => {
      const visibleIds = new Set(visibleSectionIds);
      let cursor = 0;
      return current.map(id => visibleIds.has(id) ? reordered[cursor++] : id);
    });
  };

  const isOnCanvas = (item: CatalogItem) =>
    (item.kind === 'output' || item.kind === 'virtual') && usedDeviceIds.has(item.id);

  const onLibraryDrop = (event: DragEvent<HTMLElement>) => {
    setSampleDropActive(false);
    const files = Array.from(event.dataTransfer.files ?? []);
    if (files.length > 0) {
      event.preventDefault();
      window.chrome?.webview?.postMessageWithAdditionalObjects?.(
        { type: 'sample-drop', payload: {} },
        files,
      );
      return;
    }

    const path = event.dataTransfer.getData('application/ruse-recording');
    if (path) {
      event.preventDefault();
      send('sample-import-path', { path });
    }
  };

  return (
    <aside
      className={`sidebar ${sampleDropActive ? 'sample-drop-active' : ''}`}
      aria-label={t('Routing library')}
      onDragOver={event => {
        const types = Array.from(event.dataTransfer.types);
        if (types.includes('Files') || types.includes('application/ruse-recording')) {
          event.preventDefault();
          event.dataTransfer.dropEffect = 'copy';
          setSampleDropActive(true);
        }
      }}
      onDragLeave={event => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setSampleDropActive(false);
        }
      }}
      onDrop={onLibraryDrop}
    >
      <div className="sidebar-heading">
        <span className="eyebrow">{t('Library')}</span>
      </div>
      {searching ? (
        <label className="search-box sidebar-search-active">
          <Search size={15} aria-hidden="true" />
          <input
            ref={searchRef}
            value={query}
            onChange={event => setQuery(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Escape') {
                event.stopPropagation();
                stopSearch();
              }
            }}
            placeholder={t('Search sources, outputs, effects…')}
            aria-label={t('Search library')}
          />
          <button className="icon-button compact" title={t('Stop search')} aria-label={t('Stop search')} onClick={stopSearch}><X size={13} /></button>
        </label>
      ) : (
        <div className="library-filter-row">
          <select
            className="library-view-select"
            value={view}
            aria-label={t('Library sections')}
            onChange={event => setView(event.target.value as View)}
          >
            {VIEWS.map(entry => <option key={entry.id} value={entry.id}>{t(entry.label)}</option>)}
          </select>
          <div className="library-views" role="tablist" aria-label={t('Library sections')}>
            {VIEWS.map(entry => (
              <button
                key={entry.id}
                type="button"
                role="tab"
                aria-selected={view === entry.id}
                className={`library-view ${view === entry.id ? 'active' : ''}`}
                onClick={() => setView(entry.id)}
              >
                {t(entry.label)}
              </button>
            ))}
          </div>
          <button className="library-search-button" title={t('Search library (Ctrl F)')} aria-label={t('Search library')} onClick={openSearch}>
            <Search size={15} />
          </button>
        </div>
      )}
      <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onSectionDragEnd}>
        <SortableContext items={visibleSectionIds} strategy={verticalListSortingStrategy}>
          <div className="library-scroll">
            {visible.map(group => (
              <section className="library-group" key={group.title || 'all'}>
                {group.title && <div className="group-title">{t(group.title)}</div>}
                {group.sections.map(section => {
                  const key = section.id;
                  const isCollapsed = section.id === 'samples'
                    ? !normalized && expanded[key] === false
                    : !expanded[key] && !normalized;
                  return (
                    <SortableSection
                      key={section.id}
                      section={section}
                      collapsed={isCollapsed}
                      reorderEnabled={!normalized}
                      hiddenRevealed={revealedSections.has(section.id)}
                      onToggleHidden={() => {
                        if (!revealedSections.has(section.id)) setExpanded(current => ({ ...current, [key]: true }));
                        toggleSectionHidden(section.id);
                      }}
                      onToggle={() => {
                        const willExpand = isCollapsed;
                        setExpanded(current => ({ ...current, [key]: willExpand }));
                      }}
                    >
                      {section.id === 'samples' && (
                        <div className="library-section-actions">
                          <button type="button" className="library-action" onClick={() => send('samples-open-folder')} title={t('Open samples folder')}>
                            <FolderOpen size={13} /> {t('Open folder')}
                          </button>
                          <button type="button" className="library-action" onClick={() => send('samples-import')} title={t('Add sample files')}>
                            <Upload size={13} /> {t('Add samples')}
                          </button>
                          <button type="button" className="library-action" onClick={() => send('samples-refresh')} title={t('Refresh samples')}>
                            <RefreshCw size={13} /> {t('Refresh')}
                          </button>
                          <span className="sample-hint">{t('Drop audio files here or open the folder.')}</span>
                        </div>
                      )}
                      {section.id === 'media' && <MediaBin media={media} waveforms={waveforms} query={deferredQuery} />}
                      {section.label === 'Saved chains' && (
                        <div className="library-section-actions">
                          <button type="button" className="library-action" onClick={onNewChain}>
                            <Plus size={13} /> {t('New chain')}
                          </button>
                        </div>
                      )}
                      {section.label === 'VST3 plugins' && (
                        <div className="library-section-actions">
                          <button type="button" className="library-action" onClick={onRescanPlugins}>
                            <RefreshCw size={13} /> {t('Rescan VST3 plugins')}
                          </button>
                        </div>
                      )}
                      {section.items.length === 0 && (section.hiddenCount ?? 0) > 0 && <p className="library-empty">{t('All items are hidden.')}</p>}
                      {section.items.map(item => <LibraryItem
                        key={`${item.kind}-${item.id}`}
                        item={item}
                        name={displayName(item)}
                        onAdd={onAdd}
                        onRename={onRename}
                        onEdit={item.kind === 'chain' ? onEditChain : undefined}
                        onDelete={item.kind === 'chain' || item.kind === 'template' ? onDelete : undefined}
                        onHide={toggleHidden}
                        hidden={hiddenKeys.has(catalogKey(item))}
                        onCanvas={isOnCanvas(item)}
                      />)}
                    </SortableSection>
                  );
                })}
              </section>
            ))}
            {visible.length === 0 && <div className="library-empty">{normalized ? t('Nothing matches “{query}”. Try a device, app, or effect name.', { query }) : t('Nothing here yet.')}</div>}
          </div>
        </SortableContext>
      </DndContext>
    </aside>
  );
}

export default memo(Sidebar);
