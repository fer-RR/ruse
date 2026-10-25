import { useEffect, useMemo, useState } from 'react';
import type { RefObject } from 'react';
import {
  AppWindow, AudioLines, ChevronDown, FileAudio, Headphones, Layers3, Mic,
  Pencil, Plus, Radio, Search, SlidersHorizontal, Speaker, X,
} from 'lucide-react';
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

function ItemIcon({ item }: { item: CatalogItem }) {
  const props = { size: 15, strokeWidth: 1.7, 'aria-hidden': true } as const;
  if (item.iconDataUrl) return <img className="library-app-icon" src={item.iconDataUrl} alt="" />;
  switch (item.kind) {
    case 'app': return <AppWindow {...props} />;
    case 'system': return <AudioLines {...props} />;
    case 'input': return <Mic {...props} />;
    case 'output': {
      const name = item.name.toLowerCase();
      return name.includes('headphone') || name.includes('headset')
        ? <Headphones {...props} />
        : <Speaker {...props} />;
    }
    case 'virtual': return <Radio {...props} />;
    case 'effect': return <SlidersHorizontal {...props} />;
    case 'chain': return <Layers3 {...props} />;
    case 'recorder': return <FileAudio {...props} />;
  }
}

function LibraryItem({ item, name, onAdd, onEdit, onRename }: {
  item: CatalogItem;
  name: string;
  onAdd: (item: CatalogItem) => void;
  onEdit?: (item: CatalogItem) => void;
  onRename?: (item: CatalogItem) => void;
}) {
  const active = useTitleActive(name) || item.active === true;
  return (
    <div
      className={`library-item item-${item.kind} ${active ? 'flowing' : ''}`}
      role="button"
      tabIndex={0}
      title={`${item.details}. Drag onto the canvas or double-click to add.`}
      draggable
      onDragStart={event => {
        event.dataTransfer.setData('application/ruse-item', JSON.stringify(item));
        event.dataTransfer.effectAllowed = 'copy';
      }}
      onDoubleClick={() => onAdd(item)}
      onKeyDown={event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          onAdd(item);
        }
      }}
    >
      <span className="library-icon"><ItemIcon item={item} /></span>
      <span className="library-copy">
        <span className="library-name">{name}{item.badge && <span className="item-badge">{item.badge}</span>}</span>
        <span className="library-subtitle">{item.subtitle}</span>
      </span>
      {onEdit && <button className="library-add" title={`Edit ${name}`} aria-label={`Edit ${name}`} onClick={event => { event.stopPropagation(); onEdit(item); }}>
        <Layers3 size={13} />
      </button>}
      {onRename && <button className="library-add" title={`Rename ${name}`} aria-label={`Rename ${name}`} onClick={event => { event.stopPropagation(); onRename(item); }}>
        <Pencil size={13} />
      </button>}
      <button className="library-add" title={`Add ${name}`} aria-label={`Add ${name}`} onClick={event => { event.stopPropagation(); onAdd(item); }}>
        <Plus size={14} />
      </button>
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

export default function Sidebar({ catalog, searchRef, aliases, onAdd, onRename, onNewChain, onEditChain }: {
  catalog: CatalogSnapshot;
  searchRef: RefObject<HTMLInputElement | null>;
  aliases: Record<string, string>;
  onAdd: (item: CatalogItem) => void;
  onRename: (item: CatalogItem) => void;
  onNewChain: () => void;
  onEditChain: (item: CatalogItem) => void;
}) {
  const [query, setQuery] = useState('');
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const groups = useMemo<Group[]>(() => [
    {
      title: 'Sources',
      sections: [
        { label: 'Applications', items: [systemItem, ...catalog.apps] },
        { label: 'Capture devices', items: catalog.inputs },
      ],
    },
    {
      title: 'Destinations',
      sections: [
        { label: 'Output devices', items: catalog.outputs },
        { label: 'Virtual outputs', items: catalog.virtuals },
        { label: 'Recording', items: [recorderItem] },
      ],
    },
    {
      title: 'Processing',
      sections: [
        { label: 'Effects', items: catalog.effects },
        { label: 'Saved chains', items: catalog.chains },
      ],
    },
  ], [catalog]);

  useEffect(() => {
    const focusSection = (event: Event) => {
      const label = (event as CustomEvent<{ label?: string }>).detail?.label;
      if (!label) return;
      const target = groups.flatMap(group => group.sections.map(section => ({ group: group.title, ...section })))
        .find(section => section.label === label);
      if (!target) return;
      setQuery('');
      setCollapsed(current => ({ ...current, [`${target.group}-${target.label}`]: false }));
      requestAnimationFrame(() => requestAnimationFrame(() => {
        document.querySelector(`[data-section-label="${target.label}"]`)
          ?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      }));
    };

    window.addEventListener('ruse-focus-section', focusSection);
    return () => window.removeEventListener('ruse-focus-section', focusSection);
  }, [groups]);

  const displayName = (item: CatalogItem) => aliases[catalogKey(item)]?.trim() || item.name;
  const normalized = query.trim().toLowerCase();
  const filtered = groups.map(group => ({
    ...group,
    sections: group.sections.map(section => ({
      ...section,
      items: normalized
        ? section.items.filter(item =>
            `${displayName(item)} ${item.name} ${item.subtitle} ${item.details}`.toLowerCase().includes(normalized))
        : section.items,
    })).filter(section => section.items.length > 0),
  })).filter(group => group.sections.length > 0);

  return (
    <aside className="sidebar" aria-label="Routing library">
      <div className="sidebar-heading">
        <div>
          <span className="eyebrow">Library</span>
          <h1>Build a route</h1>
        </div>
        <span className="library-hint">Drag or double-click</span>
      </div>
      <label className="search-box">
        <Search size={15} aria-hidden="true" />
        <input ref={searchRef} value={query} onChange={event => setQuery(event.target.value)} placeholder="Search sources, outputs, effects…" aria-label="Search library" />
        {query && <button className="icon-button compact" aria-label="Clear search" onClick={() => setQuery('')}><X size={13} /></button>}
        <kbd>Ctrl F</kbd>
      </label>
      <div className="library-scroll">
        {filtered.map(group => (
          <section className="library-group" key={group.title}>
            <div className="group-title">{group.title}</div>
            {group.sections.map(section => {
              const key = `${group.title}-${section.label}`;
              const isCollapsed = collapsed[key] && !normalized;
              return (
                <div className="library-section" key={key} data-section-label={section.label}>
                  <button className="section-toggle" onClick={() => setCollapsed(current => ({ ...current, [key]: !current[key] }))} aria-expanded={!isCollapsed}>
                    <span>{section.label}</span><span className="section-count">{section.items.length}</span><ChevronDown size={13} className={isCollapsed ? 'collapsed' : ''} />
                  </button>
                  {!isCollapsed && <div className="section-items">
                    {section.items.map(item => <LibraryItem
                      key={`${item.kind}-${item.id}`}
                      item={item}
                      name={displayName(item)}
                      onAdd={onAdd}
                      onRename={onRename}
                      onEdit={item.kind === 'chain' ? onEditChain : undefined}
                    />)}
                    {section.label === 'Saved chains' && <button className="new-chain-button" onClick={onNewChain}><Plus size={13} /> New chain</button>}
                  </div>}
                </div>
              );
            })}
          </section>
        ))}
        {filtered.length === 0 && <div className="library-empty">Nothing matches “{query}”. Try a device, app, or effect name.</div>}
      </div>
    </aside>
  );
}
