import { useEffect, useMemo, useState } from 'react';
import type { RefObject } from 'react';
import {
  AppWindow, AudioLines, Check, ChevronDown, FileAudio, Headphones,
  Layers3, Mic, Pencil, Plus, Puzzle, Radio, Search, SlidersHorizontal, Speaker, X,
} from 'lucide-react';
import { useI18n } from '../i18n';
import { catalogKey } from '../library';
import { useTitleActive } from '../telemetry';
import type { CatalogItem, CatalogSnapshot } from '../types';

const systemItem: CatalogItem = {
  id: 'system', kind: 'system', name: 'System audio', subtitle: 'Windows loopback',
  details: 'Capture everything playing through the default output',
};

const recorderItem: CatalogItem = {
  id: 'recorder', kind: 'recorder', name: 'Record to file', subtitle: 'WAV, MP3 or FLAC',
  details: 'Add a file destination for a routed mix',
};

const filePlayerItem: CatalogItem = {
  id: 'file-player', kind: 'file-player', name: 'File player', subtitle: 'WAV, MP3, FLAC',
  details: 'Play an audio file into a route',
};

function ItemIcon({ item }: { item: CatalogItem }) {
  const props = { size: 15, strokeWidth: 1.7, 'aria-hidden': true } as const;
  if (item.iconDataUrl) return <img className="library-app-icon" src={item.iconDataUrl} alt="" />;
  switch (item.kind) {
    case 'app': return <AppWindow {...props} />;
    case 'system': return <AudioLines {...props} />;
    case 'input': return item.virtual ? <Radio {...props} /> : <Mic {...props} />;
    case 'output': {
      const name = item.name.toLowerCase();
      return name.includes('headphone') || name.includes('headset') || name.includes('fone')
        ? <Headphones {...props} />
        : <Speaker {...props} />;
    }
    case 'virtual': return <Radio {...props} />;
    case 'effect': return <SlidersHorizontal {...props} />;
    case 'chain': return <Layers3 {...props} />;
    case 'vst3': return <Puzzle {...props} />;
    case 'file-player': return <FileAudio {...props} />;
    case 'recorder': return <FileAudio {...props} />;
  }
}

function LibraryItem({ item, name, onAdd, onEdit, onRename, onCanvas }: {
  item: CatalogItem;
  name: string;
  onAdd: (item: CatalogItem) => void;
  onEdit?: (item: CatalogItem) => void;
  onRename?: (item: CatalogItem) => void;
  onCanvas?: boolean;
}) {
  const { t } = useI18n();
  const active = useTitleActive(name) || item.active === true;
  return (
    <div
      className={`library-item item-${item.kind} ${item.kind === 'input' && item.virtual ? 'item-virtual-bus' : ''} ${active ? 'flowing' : ''}`}
      role="button"
      tabIndex={0}
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
      onDoubleClick={event => { if (event.target === event.currentTarget && !onCanvas) onAdd(item); }}
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
          {t(item.subtitle)}
        </span>
      </span>
      {onCanvas
        ? <span className="library-add library-added" title={t('Already on the canvas')}><Check size={14} /></span>
        : <span className="library-actions">
            {onEdit && <button className="library-add" title={t('Edit {name}', { name })} aria-label={t('Edit {name}', { name })} onClick={event => { event.stopPropagation(); onEdit(item); }}>
              <Layers3 size={13} />
            </button>}
            {onRename && <button className="library-add" title={t('Rename {name}', { name })} aria-label={t('Rename {name}', { name })} onClick={event => { event.stopPropagation(); onRename(item); }}>
              <Pencil size={13} />
            </button>}
            <button className="library-add" title={t('Add {name}', { name })} aria-label={t('Add {name}', { name })} onClick={event => { event.stopPropagation(); onAdd(item); }}>
              <Plus size={14} />
            </button>
          </span>}
    </div>
  );
}

interface Section {
  label: string;
  items: CatalogItem[];
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

export default function Sidebar({ catalog, searchRef, aliases, usedDeviceIds, onAdd, onRename, onNewChain, onEditChain, onRescanPlugins }: {
  catalog: CatalogSnapshot;
  searchRef: RefObject<HTMLInputElement | null>;
  aliases: Record<string, string>;
  usedDeviceIds: Set<string>;
  onAdd: (item: CatalogItem) => void;
  onRename: (item: CatalogItem) => void;
  onNewChain: () => void;
  onEditChain: (item: CatalogItem) => void;
  onRescanPlugins: () => void;
}) {
  const { t } = useI18n();
  const [query, setQuery] = useState('');
  const [view, setView] = useState<View>('all');
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [searching, setSearching] = useState(false);

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
    const physicalInputs = catalog.inputs.filter(item => !item.virtual && item.provider !== 'ASIO');
    const asioInputs = catalog.inputs.filter(item => item.provider === 'ASIO');
    const virtualInputs = catalog.inputs.filter(item => item.virtual);
    const physicalOutputs = catalog.outputs.filter(item => item.provider !== 'ASIO');
    const asioOutputs = catalog.outputs.filter(item => item.provider === 'ASIO');
    return {
      applications: { label: 'Applications', items: [systemItem, ...catalog.apps] },
      physicalOutputs: { label: 'Speakers & headphones', items: physicalOutputs },
      physicalInputs: { label: 'Microphones & inputs', items: physicalInputs },
      recording: { label: 'Record & Play', items: [recorderItem, filePlayerItem] },
      virtualBuses: { label: 'Virtual buses', items: virtualInputs },
      virtualInputs: { label: 'Virtual inputs', items: catalog.virtuals },
      asioOutputs: { label: 'ASIO outputs', items: asioOutputs },
      asioInputs: { label: 'ASIO inputs', items: asioInputs },
      effects: { label: 'Effects', items: catalog.effects },
      plugins: { label: 'VST3 plugins', items: catalog.vst3Plugins },
      chains: { label: 'Saved chains', items: catalog.chains },
    };
  }, [catalog]);

  const groups = useMemo<Group[]>(() => [
    {
      title: 'Sources',
      sections: [
        catalogSections.applications,
        catalogSections.physicalInputs,
        catalogSections.virtualBuses,
        catalogSections.asioInputs,
      ],
    },
    {
      title: 'Destinations',
      sections: [
        catalogSections.physicalOutputs,
        catalogSections.recording,
        catalogSections.virtualInputs,
        catalogSections.asioOutputs,
      ],
    },
    {
      title: 'Processing',
      sections: [catalogSections.effects, catalogSections.plugins, catalogSections.chains],
    },
  ], [catalogSections]);

  const allSections = useMemo(() => [
    catalogSections.applications,
    catalogSections.physicalOutputs,
    catalogSections.physicalInputs,
    catalogSections.recording,
    catalogSections.virtualBuses,
    catalogSections.virtualInputs,
    catalogSections.asioOutputs,
    catalogSections.asioInputs,
    catalogSections.effects,
    catalogSections.plugins,
    catalogSections.chains,
  ].filter(section => section.items.length > 0), [catalogSections]);

  useEffect(() => {
    const focusSection = (event: Event) => {
      const label = (event as CustomEvent<{ label?: string }>).detail?.label;
      if (!label) return;
      const target = groups.flatMap(group => group.sections.map(section => ({ group: group.title, ...section })))
        .find(section => section.label === label);
      if (!target) return;
      setQuery('');
      setView('all');
      setExpanded(current => ({ ...current, [`${target.group}-${target.label}`]: true }));
      requestAnimationFrame(() => requestAnimationFrame(() => {
        document.querySelector(`[data-section-label="${target.label}"]`)
          ?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      }));
    };

    window.addEventListener('ruse-focus-section', focusSection);
    return () => window.removeEventListener('ruse-focus-section', focusSection);
  }, [groups]);

  const displayName = (item: CatalogItem) => aliases[catalogKey(item)]?.trim() || t(item.name);
  const normalized = query.trim().toLowerCase();
  const filtered = groups.map(group => ({
    ...group,
    sections: group.sections.map(section => ({
      ...section,
      items: normalized
        ? section.items.filter(item =>
            `${displayName(item)} ${t(item.name)} ${t(item.subtitle)} ${t(item.details)}`.toLowerCase().includes(normalized))
        : section.items,
    })).filter(section => section.items.length > 0),
  }));

  const visible = normalized
    ? filtered.filter(group => group.sections.length > 0)
    : view === 'all'
      ? [{ title: '', sections: allSections }]
      : filtered.filter(group =>
          group.sections.length > 0 &&
          ((view === 'sources' && group.title === 'Sources') ||
            (view === 'destinations' && group.title === 'Destinations') ||
            (view === 'processing' && group.title === 'Processing')));

  const isOnCanvas = (item: CatalogItem) =>
    (item.kind === 'output' || item.kind === 'virtual') && usedDeviceIds.has(item.id);

  return (
    <aside className="sidebar" aria-label={t('Routing library')}>
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
      <div className="library-scroll">
        {visible.map(group => (
          <section className="library-group" key={group.title || 'all'}>
            {group.title && <div className="group-title">{t(group.title)}</div>}
            {group.sections.map(section => {
              const key = `${group.title}-${section.label}`;
              const isCollapsed = !expanded[key] && !normalized;
              return (
                <div className="library-section" key={key} data-section-label={section.label}>
                  <button className="section-toggle" onClick={() => setExpanded(current => ({ ...current, [key]: !current[key] }))} aria-expanded={!isCollapsed}>
                    <span>{t(section.label)}</span><span className="section-count">{section.items.length}</span><ChevronDown size={13} className={isCollapsed ? 'collapsed' : ''} />
                  </button>
                  {!isCollapsed && <div className="section-items">
                    {section.items.map(item => <LibraryItem
                      key={`${item.kind}-${item.id}`}
                      item={item}
                      name={displayName(item)}
                      onAdd={onAdd}
                      onRename={onRename}
                      onEdit={item.kind === 'chain' ? onEditChain : undefined}
                      onCanvas={isOnCanvas(item)}
                    />)}
                    {section.label === 'Saved chains' && <button className="new-chain-button" onClick={onNewChain}><Plus size={13} /> {t('New chain')}</button>}
                    {section.label === 'VST3 plugins' && <button className="new-chain-button" onClick={onRescanPlugins}><Plus size={13} /> {t('Rescan VST3 plugins')}</button>}
                  </div>}
                </div>
              );
            })}
          </section>
        ))}
        {visible.length === 0 && <div className="library-empty">{normalized ? t('Nothing matches “{query}”. Try a device, app, or effect name.', { query }) : t('Nothing here yet.')}</div>}
      </div>
    </aside>
  );
}
