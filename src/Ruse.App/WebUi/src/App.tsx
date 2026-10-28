import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  Background, BackgroundVariant, MiniMap, Panel, ReactFlow, ViewportPortal,
  useNodesState, useReactFlow, useStore,
} from '@xyflow/react';
import type { Connection, Edge, EdgeChange, NodeChange } from '@xyflow/react';
import {
  AlertTriangle, ArrowUpDown, ChevronLeft, ChevronRight, Copy, Info, LayoutGrid, Maximize2,
  Minus, Pencil, Plus, Trash2, Unplug, Volume2, VolumeX, X,
} from 'lucide-react';
import { isHosted, send, subscribe } from './bridge';
import { createDemoTelemetry, demoCatalog, demoGraph, demoSettings } from './demo';
import { I18nProvider, normalizeLanguage, translate } from './i18n';
import type { RuseFlowNode } from './flow';
import { nearestFreePosition } from './layout';
import { catalogKey } from './library';
import { getNodeTelemetry, publishTelemetry, setNodeTitles, useUiTelemetry } from './telemetry';
import RuseNode from './components/RuseNode';
import RuseEdge from './components/RuseEdge';
import RuseConnectionLine from './components/RuseConnectionLine';
import Sidebar from './components/Sidebar';
import { StatusBar, TitleBar, Toolbar } from './components/Chrome';
import { ConfirmDialog, DeviceHelpDialog, DuplicateAudioDialog, PromptDialog, RemoveFrameDialog, SettingsDialog } from './components/Dialogs';
import type { SettingsTab } from './components/Dialogs';
import ChainEditorDialog from './components/ChainEditorDialog';
import WelcomeSplash from './components/WelcomeSplash';
import WelcomeWizard from './components/WelcomeWizard';
import { DIAGNOSTICS_NODE_ID, loadDiagnosticsLayout, saveDiagnosticsLayout } from './diagnostics';
import type {
  CatalogItem, CatalogSnapshot, ChainEditorRequest, FrameModel, GraphSnapshot, HistorySnapshot,
  SettingsModel, StatusMessage, TelemetrySnapshot,
} from './types';

const nodeTypes = { ruseNode: RuseNode };
const edgeTypes = { ruseEdge: RuseEdge };
const FRAME_HANDLES = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'] as const;
type FrameHandle = typeof FRAME_HANDLES[number];
const emptyCatalog: CatalogSnapshot = {
  apps: [], outputs: [], inputs: [], virtuals: [], effects: [], chains: [],
  defaultOutputId: '', defaultOutputName: '',
};
const initialSettings: SettingsModel = demoSettings;
const systemItem: CatalogItem = {
  id: 'system', kind: 'system', name: 'System audio', subtitle: 'Windows loopback',
  details: 'Capture everything playing through the default output',
};

function createDiagnosticsNode(): RuseFlowNode {
  const layout = loadDiagnosticsLayout();
  return {
    id: DIAGNOSTICS_NODE_ID,
    type: 'ruseNode',
    position: { x: layout.x, y: layout.y },
    data: {
      id: DIAGNOSTICS_NODE_ID,
      kind: 'modifier',
      flavor: 'diagnostics',
      title: 'Diagnostics',
      subtitle: 'Live engine telemetry',
      accent: '#65D6D0',
      x: layout.x,
      y: layout.y,
      volume: 1,
      muted: false,
      parameters: [],
      isRecorder: false,
      recordingPath: '',
      recordingFormat: 'Wav24',
      highlightMuted: false,
      diagnostics: true,
    },
  };
}

interface RenamePrompt { id: string; title: string }
interface FrameRemoval { frame: FrameModel; nodeIds: string[] }
interface RecorderRemoval { id: string; title: string; resolve?: (ok: boolean) => void }
interface CanvasMenu {
  x: number;
  y: number;
  kind: 'node' | 'edge' | 'pane';
  id?: string;
  flowX?: number;
  flowY?: number;
}

export default function App() {
  const flow = useReactFlow();
  const graphElement = useRef<HTMLDivElement>(null);
  const workspaceRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const [catalog, setCatalog] = useState<CatalogSnapshot>(isHosted ? emptyCatalog : demoCatalog);
  const [graph, setGraph] = useState<GraphSnapshot>(isHosted ? { version: 0, nodes: [], links: [], running: false } : demoGraph);
  const [settings, setSettings] = useState<SettingsModel>(initialSettings);
  const language = normalizeLanguage(settings.uiLanguage);
  const t = (text: string, vars?: Record<string, string | number>) => translate(language, text, vars);
  const ui = useUiTelemetry();
  const [status, setStatus] = useState<StatusMessage>({ text: isHosted ? 'Starting native audio engine.' : 'Interface preview - native engine not attached.', isError: false });
  const [alert, setAlert] = useState<StatusMessage | null>(null);
  const [dismissedWarning, setDismissedWarning] = useState('');
  const [history, setHistory] = useState<HistorySnapshot>({ canUndo: false, canRedo: false, undoLabel: '', redoLabel: '' });
  const [nodes, setNodes, onNodesChange] = useNodesState<RuseFlowNode>([]);
  const [zoom, setZoom] = useState(1);
  const [sidebarWidth, setSidebarWidth] = useState(() => {
    const saved = Number(localStorage.getItem('ruse.sidebarWidth'));
    return saved >= 240 && saved <= 460 ? saved : 304;
  });
  const [sidebarCollapsed, setSidebarCollapsed] = useState(
    () => localStorage.getItem('ruse.sidebarCollapsed') === '1',
  );
  const [showSettings, setShowSettings] = useState(false);
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('behavior');
  const [showAudioHelp, setShowAudioHelp] = useState(false);
  const [showDeviceHelp, setShowDeviceHelp] = useState(false);
  const [splashOpen, setSplashOpen] = useState(false);
  const [welcomeOpen, setWelcomeOpen] = useState(false);
  const [showClear, setShowClear] = useState(false);
  const [renamePrompt, setRenamePrompt] = useState<RenamePrompt | null>(null);
  const [canvasMenu, setCanvasMenu] = useState<CanvasMenu | null>(null);
  const [chainEditor, setChainEditor] = useState<ChainEditorRequest | null>(null);
  const [renameLibrary, setRenameLibrary] = useState<CatalogItem | null>(null);
  const [aliases, setAliases] = useState<Record<string, string>>(() => {
    try {
      return JSON.parse(localStorage.getItem('ruse.libraryAliases') ?? '{}') as Record<string, string>;
    } catch {
      return {};
    }
  });
  const [frames, setFrames] = useState<FrameModel[]>([]);
  const [altPressed, setAltPressed] = useState(false);
  const [frameDraft, setFrameDraft] = useState<FrameModel | null>(null);
  const [frameRename, setFrameRename] = useState<FrameModel | null>(null);
  const [frameRemoval, setFrameRemoval] = useState<FrameRemoval | null>(null);
  const [recorderRemoval, setRecorderRemoval] = useState<RecorderRemoval | null>(null);
  const appShellRef = useRef<HTMLDivElement>(null);
  const connectingRef = useRef(false);
  const draggingRef = useRef(false);
  const pendingTelemetry = useRef<TelemetrySnapshot | null>(null);
  const pendingMoves = useRef<Map<string, { x: number; y: number; at: number  }>>(new Map());
  const framesRef = useRef<FrameModel[]>([]);
  const frameEditing = useRef(false);
  const graphSeen = useRef(false);

  const nodesRef = useRef(nodes);
  const settingsRef = useRef(settings);
  const sidebarCollapsedRef = useRef(sidebarCollapsed);
  const zCounter = useRef(1);
  useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  useEffect(() => { framesRef.current = frames; }, [frames]);
  useEffect(() => { settingsRef.current = settings; }, [settings]);
  useEffect(() => { sidebarCollapsedRef.current = sidebarCollapsed; }, [sidebarCollapsed]);
  useEffect(() => { localStorage.setItem('ruse.sidebarWidth', String(sidebarWidth)); }, [sidebarWidth]);
  useEffect(() => { localStorage.setItem('ruse.sidebarCollapsed', sidebarCollapsed ? '1' : '0'); }, [sidebarCollapsed]);
  useLayoutEffect(() => {
    workspaceRef.current?.style.setProperty('--sidebar-width', `${sidebarWidth}px`);
  }, [sidebarWidth]);
  useEffect(() => { localStorage.setItem('ruse.libraryAliases', JSON.stringify(aliases)); }, [aliases]);

  const saveFrames = (next: FrameModel[]) => {
    framesRef.current = next;
    setFrames(next);
    send('frames', { frames: next });
  };

  const snapFrameValue = (value: number) =>
    settingsRef.current.snapToGrid ? Math.round(value / 20) * 20 : Math.round(value);

  const nextFrameName = () => {
    const names = new Set(framesRef.current.map(frame => frame.name));
    let index = 1;
    while (names.has(`Group ${index}`)) index++;
    return `Group ${index}`;
  };

  const newFrameId = () => `frame-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

  useEffect(() => {
    const down = (event: KeyboardEvent) => { if (event.key === 'Alt') setAltPressed(true); };
    const up = (event: KeyboardEvent) => { if (event.key === 'Alt') setAltPressed(false); };
    const blur = () => setAltPressed(false);
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', blur);
    return () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
      window.removeEventListener('blur', blur);
    };
  }, []);

  useEffect(() => {
    const pane = graphElement.current?.querySelector<HTMLElement>('.react-flow__pane');
    if (!pane) return;
    let cleanup: (() => void) | undefined;

    const onDown = (event: PointerEvent) => {
      if (event.button !== 0 || (!event.ctrlKey && !event.metaKey)) return;
      const target = event.target as Element | null;
      if (target?.closest('.react-flow__node, .react-flow__edge, .ruse-frame')) return;

      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation();

      const start = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
      let rect: FrameModel = {
        id: '', name: '',
        x: snapFrameValue(start.x), y: snapFrameValue(start.y),
        width: 0, height: 0,
      };
      frameEditing.current = true;
      setFrameDraft(rect);

      const move = (moveEvent: PointerEvent) => {
        const point = flow.screenToFlowPosition({ x: moveEvent.clientX, y: moveEvent.clientY });
        const left = snapFrameValue(Math.min(start.x, point.x));
        const top = snapFrameValue(Math.min(start.y, point.y));
        const right = snapFrameValue(Math.max(start.x, point.x));
        const bottom = snapFrameValue(Math.max(start.y, point.y));
        rect = {
          id: '', name: '',
          x: left, y: top,
          width: Math.max(0, right - left), height: Math.max(0, bottom - top),
        };
        setFrameDraft(rect);
      };

      const finish = () => {
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', finish);
        window.removeEventListener('pointercancel', finish);
        cleanup = undefined;
        if (rect.width >= 60 && rect.height >= 40) {
          saveFrames([...framesRef.current, {
            ...rect,
            id: newFrameId(),
            name: nextFrameName(),
          }]);
        }
        setFrameDraft(null);
        frameEditing.current = false;
      };

      window.addEventListener('pointermove', move);
      window.addEventListener('pointerup', finish);
      window.addEventListener('pointercancel', finish);
      cleanup = () => {
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', finish);
        window.removeEventListener('pointercancel', finish);
        setFrameDraft(null);
        frameEditing.current = false;
      };
    };

    pane.addEventListener('pointerdown', onDown, true);
    return () => {
      pane.removeEventListener('pointerdown', onDown, true);
      cleanup?.();
    };
  }, [flow]);

  const resizeFrame = (event: React.PointerEvent<HTMLDivElement>, frame: FrameModel, handle: FrameHandle) => {
    if (!event.altKey) return;
    event.preventDefault();
    event.stopPropagation();
    const origin = { ...frame };
    frameEditing.current = true;
    let next = { ...frame };

    const move = (moveEvent: PointerEvent) => {
      const point = flow.screenToFlowPosition({ x: moveEvent.clientX, y: moveEvent.clientY });
      let left = origin.x;
      let top = origin.y;
      let right = origin.x + origin.width;
      let bottom = origin.y + origin.height;
      const x = snapFrameValue(point.x);
      const y = snapFrameValue(point.y);
      if (handle.includes('w')) left = Math.min(x, right - 60);
      if (handle.includes('e')) right = Math.max(x, left + 60);
      if (handle.includes('n')) top = Math.min(y, bottom - 40);
      if (handle.includes('s')) bottom = Math.max(y, top + 40);
      right = Math.max(left + 60, right);
      bottom = Math.max(top + 40, bottom);
      next = { ...origin, x: left, y: top, width: right - left, height: bottom - top };
      framesRef.current = framesRef.current.map(item => item.id === frame.id ? next : item);
      setFrames(framesRef.current);
    };

    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      saveFrames(framesRef.current);
      frameEditing.current = false;
    };

    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
  };

  const removeFrameOnly = (frame: FrameModel) => {
    saveFrames(framesRef.current.filter(item => item.id !== frame.id));
  };

  const requestRemoveFrame = (frame: FrameModel) => {
    const nodeIds = nodes.filter(node => {
      if (node.data.diagnostics) return false;
      const width = node.measured?.width ?? 204;
      const height = node.measured?.height ?? 140;
      const x = node.position.x + width / 2;
      const y = node.position.y + height / 2;
      return x >= frame.x && x <= frame.x + frame.width && y >= frame.y && y <= frame.y + frame.height;
    }).map(node => node.id);
    if (nodeIds.length === 0) removeFrameOnly(frame);
    else setFrameRemoval({ frame, nodeIds });
  };
  useEffect(() => subscribe((type, payload) => {
    switch (type) {
      case 'catalog': setCatalog(payload as CatalogSnapshot); break;
      case 'graph': setGraph(payload as GraphSnapshot); break;
      case 'telemetry':
        if (connectingRef.current || draggingRef.current) {
          pendingTelemetry.current = payload as TelemetrySnapshot;
        } else {
          publishTelemetry(payload as TelemetrySnapshot);
        }
        break;
      case 'settings': setSettings(payload as SettingsModel); break;
      case 'history': setHistory(payload as HistorySnapshot); break;
      case 'chain-edit': setChainEditor(payload as ChainEditorRequest); break;
      case 'status': setStatus(payload as StatusMessage); break;
      case 'alert': setAlert(payload as StatusMessage); break;
      case 'splash': setSplashOpen(true); break;
      case 'welcome': setWelcomeOpen(true); break;
    }
  }), []);

  useEffect(() => {
    send('ready');
    if (!isHosted) {
      publishTelemetry(createDemoTelemetry(0));
      const timer = window.setInterval(() => publishTelemetry(createDemoTelemetry(performance.now())), 100);
      return () => window.clearInterval(timer);
    }
    return undefined;
  }, []);

  useEffect(() => {
    const current = new Map(nodesRef.current.map(node => [node.id, node]));
    const wasSeen = graphSeen.current;
    const viewport = flow.getViewport();
    const now = performance.now();
    const graphIds = new Set(graph.nodes.map(node => node.id));
    for (const id of pendingMoves.current.keys()) {
      if (!graphIds.has(id)) pendingMoves.current.delete(id);
      else if (now - pendingMoves.current.get(id)!.at > 2500) pendingMoves.current.delete(id);
    }
    const mapped: RuseFlowNode[] = graph.nodes.map(model => {
      const existing = current.get(model.id);
      let x = model.x;
      let y = model.y;
      const pending = pendingMoves.current.get(model.id);
      if (pending) {
        if (Math.abs(pending.x - x) < 0.5 && Math.abs(pending.y - y) < 0.5) pendingMoves.current.delete(model.id);
        else {
          x = pending.x;
          y = pending.y;
        }
      }
      return {
        id: model.id,
        type: 'ruseNode',
        position: { x, y },
        selected: existing?.selected ?? false,
        zIndex: existing?.zIndex ?? ++zCounter.current,
        data: {
          ...model,
          highlightMuted: settingsRef.current.highlightMutedNodes,
          transcriberModels: settingsRef.current.transcriberModels,
        },
      } satisfies RuseFlowNode;
    });

    if (settingsRef.current.showDiagnostics) {
      const existing = current.get(DIAGNOSTICS_NODE_ID);
      mapped.push(existing ?? { ...createDiagnosticsNode(), zIndex: ++zCounter.current });
    }

    setNodes(mapped);

    if (!frameEditing.current) setFrames(graph.frames ?? []);

    if (!wasSeen && graph.version > 0) {
      graphSeen.current = true;
      if (graph.nodes.length > 0) {
        const timer = window.setTimeout(() => flow.fitView({ padding: 0.18 }), 60);
        return () => window.clearTimeout(timer);
      }
    }

    if (wasSeen) {
      requestAnimationFrame(() => {
        const next = flow.getViewport();
        if (Math.abs(next.x - viewport.x) > 0.5 ||
            Math.abs(next.y - viewport.y) > 0.5 ||
            Math.abs(next.zoom - viewport.zoom) > 0.001) {
          flow.setViewport(viewport);
        }
      });
    }

    return undefined;
  }, [graph.version, setNodes, flow]);

  useEffect(() => {
    setNodes(current => {
      const exists = current.some(node => node.id === DIAGNOSTICS_NODE_ID);
      if (settings.showDiagnostics && !exists) {
        return [...current, { ...createDiagnosticsNode(), zIndex: ++zCounter.current }];
      }
      if (!settings.showDiagnostics && exists) {
        return current.filter(node => node.id !== DIAGNOSTICS_NODE_ID);
      }
      return current;
    });
  }, [settings.showDiagnostics, setNodes]);

  useEffect(() => {
    setNodes(current => current.map(node => node.data.isTranscriber
      ? { ...node, data: { ...node.data, transcriberModels: settings.transcriberModels } }
      : node));
  }, [settings.transcriberModels, setNodes]);

  useEffect(() => {
    const openSettings = (event: Event) => {
      const detail = (event as CustomEvent<{ tab?: SettingsTab  }>).detail;
      setSettingsTab(detail?.tab ?? 'behavior');
      setShowSettings(true);
    };
    window.addEventListener('ruse-open-settings', openSettings);
    return () => window.removeEventListener('ruse-open-settings', openSettings);
  }, []);

  useEffect(() => {
    const renameNode = (event: Event) => {
      const detail = (event as CustomEvent<{ id?: string; title?: string; chain?: boolean  }>).detail;
      if (!detail?.id) return;
      if (detail.chain) send('chain-open', { id: detail.id });
      else setRenamePrompt({ id: detail.id, title: detail.title ?? '' });
    };
    window.addEventListener('ruse-rename-node', renameNode);
    return () => window.removeEventListener('ruse-rename-node', renameNode);
  }, []);

  useEffect(() => {
    const removeRecorder = (event: Event) => {
      const detail = (event as CustomEvent<RecorderRemoval>).detail;
      if (detail?.id) setRecorderRemoval(detail);
    };
    window.addEventListener('ruse-remove-recorder', removeRecorder);
    return () => window.removeEventListener('ruse-remove-recorder', removeRecorder);
  }, []);

  useEffect(() => {
    const closeDiagnostics = () => {
      const next = { ...settingsRef.current, showDiagnostics: false };
      settingsRef.current = next;
      setSettings(next);
      send('settings', next);
    };
    window.addEventListener('ruse-close-diagnostics', closeDiagnostics);
    return () => window.removeEventListener('ruse-close-diagnostics', closeDiagnostics);
  }, []);

  useEffect(() => {
    setNodes(current => current.map(node => ({
      ...node,
      data: { ...node.data, highlightMuted: settings.highlightMutedNodes },
    })));
  }, [settings.highlightMutedNodes, setNodes]);

  useEffect(() => {
    const titles: Record<string, string> = {};
    for (const node of graph.nodes) {
      titles[node.id] = node.isRecorder ? 'Record to file' : node.title;
    }
    setNodeTitles(titles);
  }, [graph.version]);

  const edges = useMemo<Edge[]>(() => graph.links.map(link => {
    const source = graph.nodes.find(node => node.id === link.from);
    return {
      id: link.id,
      source: link.from,
      target: link.to,
      sourceHandle: 'out',
      targetHandle: 'in',
      type: 'ruseEdge',
      animated: false,
      interactionWidth: 22,
      style: { stroke: source?.accent ?? '#5B8DEF', strokeWidth: 2, opacity: 0.84 },
    };
  }), [graph.links, graph.nodes]);

  const sendAdd = (item: CatalogItem, position: { x: number; y: number }, linkId?: string) => {
    send('add', {
      kind: item.kind,
      id: item.id,
      title: aliases[catalogKey(item)]?.trim() || t(item.name),
      processId: item.processId ?? 0,
      micHint: item.micHint ?? '',
      linkId: linkId ?? '',
      x: Math.round(position.x / 20) * 20,
      y: Math.round(position.y / 20) * 20,
    });
  };

  const findLinkAtPoint = (point: { x: number; y: number }): string | undefined => {
    const threshold = Math.max(10, 18 / Math.max(0.25, flow.getViewport().zoom));
    let closest: string | undefined;
    let closestDistance = threshold;

    document.querySelectorAll<SVGGElement>('.react-flow__edge').forEach(edge => {
      const id = edge.getAttribute('data-id');
      const path = edge.querySelector<SVGPathElement>('.react-flow__edge-path');
      if (!id || !path) return;
      try {
        const length = path.getTotalLength();
        if (!Number.isFinite(length) || length <= 0) return;
        const samples = Math.max(24, Math.min(120, Math.ceil(length / 8)));
        for (let index = 0; index <= samples; index++) {
          const sample = path.getPointAtLength(length * index / samples);
          const distance = Math.hypot(sample.x - point.x, sample.y - point.y);
          if (distance < closestDistance) {
            closestDistance = distance;
            closest = id;
          }
        }
      } catch {
        // A path can disappear between the drop and the query during teardown.
      }
    });

    return closest;
  };

  const bringNodeToFront = (id: string) => {
    const currentTop = nodesRef.current.reduce((top, node) => Math.max(top, node.zIndex ?? 0), 0);
    zCounter.current = Math.max(zCounter.current, currentTop) + 1;
    const zIndex = zCounter.current;
    setNodes(current => current.map(node => node.id === id ? { ...node, zIndex } : node));
  };

  const findFreeSpot = (position: { x: number; y: number }) => {
    const others = nodesRef.current.map(node => ({
      id: node.id,
      x: node.position.x,
      y: node.position.y,
      width: node.measured?.width ?? 204,
      height: node.measured?.height ?? 140,
    }));
    return nearestFreePosition({ id: 'new', x: position.x, y: position.y, width: 204, height: 140 }, others);
  };

  const quickAdd = (item: CatalogItem) => {
    const bounds = graphElement.current?.getBoundingClientRect();
    const position = bounds
      ? flow.screenToFlowPosition({ x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 })
      : { x: 60 + graph.nodes.length * 30, y: 60 + graph.nodes.length * 30 };
    sendAdd(item, findFreeSpot({ x: position.x - 102, y: position.y - 60 }));
  };

  const tidyGraph = () => {
    const rank = { source: 0, modifier: 1, output: 2 };
    const columns = [80, 420, 760];
    const nextY = [80, 80, 80];
    const positions = [...nodesRef.current]
      .filter(node => !node.data.diagnostics)
      .sort((a, b) => rank[a.data.kind] - rank[b.data.kind] ||
        a.position.y - b.position.y || a.position.x - b.position.x)
      .map(node => {
        const column = rank[node.data.kind];
        const position = { id: node.id, x: columns[column], y: nextY[column] };
        nextY[column] += (node.measured?.height ?? 140) + 44;
        return position;
      });

    const byId = new Map(positions.map(position => [position.id, position]));
    setNodes(current => current.map(node => {
      const position = byId.get(node.id);
      return position ? { ...node, position: { x: position.x, y: position.y } } : node;
    }));
    send('move-many', { positions });
    setCanvasMenu(null);
  };

  const duplicateSelected = () => {
    const selected = nodesRef.current.find(node => node.selected && !node.data.diagnostics);
    if (selected) send('duplicate', { id: selected.id });
  };

  const muteSelected = () => {
    const selected = nodesRef.current.find(node => node.selected && !node.data.diagnostics);
    if (selected) send('mute', { id: selected.id, value: !selected.data.muted });
  };

  const onConnect = (connection: Connection) => {
    if (connection.source && connection.target) {
      send('connect', { from: connection.source, to: connection.target });
    }
  };

  const nodeById = useMemo(() => new Map(graph.nodes.map(node => [node.id, node])), [graph.nodes]);
  const isValidConnection = useCallback((connection: Connection | Edge) => {
    const source = connection.source ? nodeById.get(connection.source) : undefined;
    const target = connection.target ? nodeById.get(connection.target) : undefined;
    if (!source || !target || source.id === target.id) return false;
    return (source.kind === 'source' && target.kind !== 'source') ||
           (source.kind === 'modifier' && target.kind !== 'source');
  }, [nodeById]);

  const setInteracting = (value: boolean) => {
    appShellRef.current?.classList.toggle('is-interacting', value);
  };

  const flushPendingTelemetry = () => {
    if (pendingTelemetry.current) {
      publishTelemetry(pendingTelemetry.current);
      pendingTelemetry.current = null;
    }
  };

  const finishConnecting = () => {
    connectingRef.current = false;
    setInteracting(false);
    flushPendingTelemetry();
  };

  const onNodeDragStop = (_event: unknown, node: RuseFlowNode) => {
    const others = nodesRef.current.map(item => ({
      id: item.id,
      x: item.position.x,
      y: item.position.y,
      width: item.measured?.width ?? 204,
      height: item.measured?.height ?? 120,
    }));
    const moving = others.find(item => item.id === node.id);
    if (!moving) {
      draggingRef.current = false;
      setInteracting(false);
      flushPendingTelemetry();
      return;
    }
    const free = nearestFreePosition({ ...moving, x: node.position.x, y: node.position.y }, others);
    if (free.x !== node.position.x || free.y !== node.position.y) {
      setNodes(current => current.map(item => item.id === node.id ? { ...item, position: free } : item));
    }

    draggingRef.current = false;
    setInteracting(false);
    flushPendingTelemetry();

    if (node.data.diagnostics) {
      saveDiagnosticsLayout({ ...loadDiagnosticsLayout(), x: free.x, y: free.y });
      return;
    }

    pendingMoves.current.set(node.id, { ...free, at: performance.now() });
    send('move', { id: node.id, x: free.x, y: free.y });
  };

  const onNodesDelete = (deleted: RuseFlowNode[]) => {
    for (const node of deleted) {
      if (!node.data.diagnostics) send('remove', { id: node.id });
    }
  };

  const onEdgesChange = (changes: EdgeChange<Edge>[]) => {
    for (const change of changes) {
      if (change.type === 'remove') send('disconnect', { id: change.id });
    }
  };

  const fitGraph = () => flow.fitView({ padding: 0.18, duration: 180 });

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement;
      const textInput = target instanceof HTMLInputElement &&
        !['range', 'checkbox', 'radio', 'button', 'submit'].includes(target.type);
      const editing = textInput || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable;
      if (event.key === 'F1') {
        event.preventDefault();
        setSettingsTab('shortcuts');
        setShowSettings(true);
        return;
      }
      if (editing) return;
      const modifier = event.ctrlKey || event.metaKey;
      if (modifier && event.key.toLowerCase() === 'z') {
        event.preventDefault();
        send(event.shiftKey ? 'redo' : 'undo');
      } else if (modifier && event.key.toLowerCase() === 'y') {
        event.preventDefault();
        send('redo');
      } else if (modifier && event.key.toLowerCase() === 'd') {
        event.preventDefault();
        duplicateSelected();
      } else if (modifier && event.key.toLowerCase() === 'f') {
        event.preventDefault();
        if (sidebarCollapsedRef.current) {
          setSidebarCollapsed(false);
          window.setTimeout(() => window.dispatchEvent(new CustomEvent('ruse-focus-search')), 60);
        } else {
          window.dispatchEvent(new CustomEvent('ruse-focus-search'));
        }
      } else if (modifier && event.key.toLowerCase() === 's') {
        event.preventDefault();
        send('save-preset');
      } else if (modifier && event.key.toLowerCase() === 'o') {
        event.preventDefault();
        send('load-preset');
      } else if (modifier && ['1', '2', '3', '4', '5'].includes(event.key)) {
        event.preventDefault();
        const sections: Record<string, string> = {
          '1': 'Applications',
          '2': 'Speakers & headphones',
          '3': 'Microphones & inputs',
          '4': 'Virtual inputs',
          '5': 'Effects',
        };
        window.dispatchEvent(new CustomEvent('ruse-focus-section', { detail: { label: sections[event.key] } }));
      } else if (event.key === '?' || (event.key === '/' && event.shiftKey)) {
        event.preventDefault();
        setSettingsTab('shortcuts');
        setShowSettings(true);
      } else if (event.key.toLowerCase() === 'f') {
        fitGraph();
      } else if (event.key.toLowerCase() === 'm') {
        muteSelected();
      } else if (event.key.toLowerCase() === 't') {
        tidyGraph();
      } else if (event.key === '0') {
        flow.zoomTo(1, { duration: 160 });
      } else if (event.key === '+' || event.key === '=') {
        flow.zoomIn({ duration: 120 });
      } else if (event.key === '-') {
        flow.zoomOut({ duration: 120 });
      } else if (event.key === 'Escape') {
        setCanvasMenu(null);
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [flow]);

  const startSidebarResize = (event: React.PointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    const startX = event.clientX;
    const startWidth = sidebarWidth;
    let nextWidth = startWidth;
    document.body.classList.add('sidebar-resizing');

    // Move the grid track directly instead of re-rendering the whole app on
    // every pointer sample; the graph stays smooth while the library resizes.
    const move = (moveEvent: PointerEvent) => {
      nextWidth = Math.min(460, Math.max(240, startWidth + moveEvent.clientX - startX));
      workspaceRef.current?.style.setProperty('--sidebar-width', `${nextWidth}px`);
    };

    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      document.body.classList.remove('sidebar-resizing');
      setSidebarWidth(nextWidth);
    };

    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
  };

  const anyNodeOffscreen = useStore(state => {
    if (!state.width || !state.height) return false;
    const [tx, ty, zoomValue] = state.transform;
    const left = -tx / zoomValue;
    const top = -ty / zoomValue;
    const right = (state.width - tx) / zoomValue;
    const bottom = (state.height - ty) / zoomValue;
    return state.nodes.some(node => {
      const width = node.measured?.width ?? 204;
      const height = node.measured?.height ?? 140;
      return node.position.x + width < left ||
        node.position.x > right ||
        node.position.y + height < top ||
        node.position.y > bottom;
    });
  });
  const showMiniMap = nodes.length > 0 && settings.minimapMode !== 1 && anyNodeOffscreen;
  const running = ui.running || graph.running;
  const primaryWarning = graph.warnings?.find(warning => warning.severity === 'warning') ?? graph.warnings?.[0];
  const warningSignature = primaryWarning
    ? `${primaryWarning.severity}|${primaryWarning.sourceId}|${primaryWarning.outputId}|${primaryWarning.deviceName}|${primaryWarning.message}`
    : '';
  useEffect(() => {
    if (!warningSignature) setDismissedWarning('');
  }, [warningSignature]);
  const warningVisible = !!primaryWarning && warningSignature !== dismissedWarning;
  const usedDeviceIds = useMemo(
    () => new Set(graph.nodes.filter(node => node.deviceId).map(node => node.deviceId!)),
    [graph.nodes],
  );
  const menuNode = canvasMenu?.kind === 'node' ? nodes.find(node => node.id === canvasMenu.id) : undefined;
  const menuLink = canvasMenu?.kind === 'edge' ? graph.links.find(link => link.id === canvasMenu.id) : undefined;
  const menuDbVertical = menuNode?.data.isDbMeter
    ? (getNodeTelemetry(menuNode.id)?.dbMeterVertical ?? true)
    : true;
  const menuLeft = canvasMenu ? Math.min(canvasMenu.x, window.innerWidth - 230) : 0;
  const menuTop = canvasMenu ? Math.min(canvasMenu.y, window.innerHeight - 300) : 0;

  return (
    <I18nProvider language={language }>
    <div ref={appShellRef} className={`app-shell ${settings.signalGlow ? 'signal-glow' : ''} ${settings.highlightCableOnHover ? 'cable-hover' : ''} ${settings.cableAnimation ? 'cable-animation' : ''} ${altPressed ? 'alt' : ''}`}>
      <TitleBar />
      <Toolbar
        running={running}
        micMuted={ui.micMuted}
        canUndo={history.canUndo}
        canRedo={history.canRedo}
        undoLabel={history.undoLabel}
        redoLabel={history.redoLabel}
        onTransport={value => send('transport', { running: value })}
        onRefresh={() => send('refresh')}
        onSave={() => send('save-preset')}
        onLoad={() => send('load-preset')}
        onClear={() => setShowClear(true)}
        onUndo={() => send('undo')}
        onRedo={() => send('redo')}
        onTidy={tidyGraph}
        onMicMute={() => send('mic-mute', { value: !ui.micMuted })}
        onDeviceHelp={() => setShowDeviceHelp(true)}
        onSettings={() => { setSettingsTab('behavior'); setShowSettings(true); }}
      />
      {alert && <div className={`alert-bar ${alert.isError ? 'error' : ''}` }><span>{alert.text}</span><div className="alert-actions"><button onClick={() => send('open-log') }>{t('Open log')}</button><button onClick={() => setAlert(null) }>{t('Dismiss')}</button></div></div>}
      {warningVisible && primaryWarning && (
        <div className={`routing-warning-bar ${primaryWarning.severity === 'info' ? 'info' : ''}`} role="alert">
          {primaryWarning.severity === 'info' ? <Info size={15} /> : <AlertTriangle size={15} />}
          <span className="routing-warning-copy">{primaryWarning.message}</span>
          {(graph.warnings?.length ?? 0) > 1 && <span className="routing-warning-count">+{(graph.warnings?.length ?? 1) - 1}</span>}
          <button className="routing-warning-action" onClick={() => setShowAudioHelp(true) }>{t('How to fix')}</button>
          <button className="routing-warning-dismiss" title={t('Dismiss warning')} aria-label={t('Dismiss warning')} onClick={() => setDismissedWarning(warningSignature)}><X size={14} /></button>
        </div>
      )}
      <div ref={workspaceRef} className={`workspace ${sidebarCollapsed ? 'sidebar-collapsed' : ''}`}>
        {sidebarCollapsed ? (
          <aside className="sidebar-rail" aria-label={t('Library')} title={t('Show library')} onClick={() => setSidebarCollapsed(false)}>
            <button className="sidebar-rail-button" title={t('Show library')} aria-label={t('Show library')} onClick={(event) => { event.stopPropagation(); setSidebarCollapsed(false); }}>
              <ChevronRight size={14} />
            </button>
            <span className="sidebar-rail-label">{t('Library')}</span>
          </aside>
        ) : (
          <>
            <Sidebar
              catalog={catalog}
              searchRef={searchRef}
              aliases={aliases}
              usedDeviceIds={usedDeviceIds}
              onAdd={quickAdd}
              onRename={setRenameLibrary}
              onNewChain={() => setChainEditor({ name: 'My chain', elements: [] })}
              onEditChain={item => setChainEditor({
                name: item.name,
                previousName: item.name,
                elements: (item.elements ?? []).map(element => ({ typeId: element.typeId, parameters: { ...element.parameters } })),
              })}
            />
            <div className="sidebar-edge">
              <div className="sidebar-resizer" role="separator" aria-label={t('Resize library')} title={t('Drag to resize · double-click to hide')} onDoubleClick={() => setSidebarCollapsed(true)} onPointerDown={startSidebarResize} />
              <button className="sidebar-collapse-tab" title={t('Hide library')} aria-label={t('Hide library')} onClick={() => setSidebarCollapsed(true)} onPointerDown={event => event.stopPropagation()}>
                <ChevronLeft size={14} />
              </button>
            </div>
          </>
        )}
        <main
          className="graph-area"
          ref={graphElement}
          onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; }}
          onDrop={event => {
            event.preventDefault();
            const raw = event.dataTransfer.getData('application/ruse-item');
            if (!raw) return;
            const item = JSON.parse(raw) as CatalogItem;
            const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
            const canInsert = item.kind === 'effect' || item.kind === 'chain';
            const linkId = canInsert ? findLinkAtPoint(position) : undefined;
            sendAdd(
              item,
              linkId ? { x: position.x - 102, y: position.y - 60 } : findFreeSpot({ x: position.x, y: position.y }),
              linkId,
            );
          }}
        >
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            edgeTypes={edgeTypes}
            onNodesChange={onNodesChange as (changes: NodeChange<RuseFlowNode>[]) => void}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            onConnectStart={() => { connectingRef.current = true; setInteracting(true); }}
            onConnectEnd={finishConnecting}
            connectOnClick
            connectionRadius={42}
            onNodeClick={(_event, node) => { bringNodeToFront(node.id); setCanvasMenu(null); }}
            onNodeDragStart={(_event, node) => {
              draggingRef.current = true;
              setInteracting(true);
              bringNodeToFront(node.id);
            }}
            onNodeDragStop={onNodeDragStop}
            onNodesDelete={onNodesDelete}
            onBeforeDelete={async ({ nodes: deleting, edges }) => {
              const hasDiagnostics = deleting.some(node => node.data.diagnostics);
              const deletable = deleting.filter(node => !node.data.diagnostics);
              const active = deletable.filter(node =>
                node.data.isRecorder && getNodeTelemetry(node.id)?.recordingStatus === 'REC');
              if (active.length > 0) {
                const confirmed = await new Promise<boolean>(resolve => {
                  setRecorderRemoval({ id: active[0].id, title: active[0].data.title, resolve });
                });
                if (!confirmed) return false;
              }
              return hasDiagnostics ? { nodes: deletable, edges } : true;
            }}
            onNodeContextMenu={(event, node) => {
              event.preventDefault();
              if (node.data.diagnostics) return;
              setCanvasMenu({ x: event.clientX, y: event.clientY, kind: 'node', id: node.id });
            }}
            onEdgeContextMenu={(event, edge) => {
              event.preventDefault();
              setCanvasMenu({ x: event.clientX, y: event.clientY, kind: 'edge', id: edge.id });
            }}
            onPaneContextMenu={event => {
              event.preventDefault();
              const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
              setCanvasMenu({ x: event.clientX, y: event.clientY, kind: 'pane', flowX: position.x, flowY: position.y });
            }}
            onPaneClick={() => setCanvasMenu(null)}
            onMove={(_event, viewport) => setZoom(viewport.zoom)}
            isValidConnection={isValidConnection}
            minZoom={0.25}
            maxZoom={2.5}
            snapToGrid={settings.snapToGrid}
            snapGrid={[20, 20]}
            zIndexMode="manual"
            deleteKeyCode={['Delete', 'Backspace']}
            connectionLineComponent={RuseConnectionLine}
            connectionLineStyle={{ stroke: '#65D6D0', strokeWidth: 2, strokeDasharray: '5 4' }}
            connectionDragThreshold={0}
            proOptions={{ hideAttribution: true }}
            defaultEdgeOptions={{ type: 'ruseEdge' }}
          >
            <Background variant={BackgroundVariant.Dots} gap={24} size={1.15} color="#222B39" />
            <ViewportPortal>
              <div className={`ruse-frame-layer ${altPressed ? 'alt' : ''}` }>
                {frameDraft && <div className="ruse-frame draft" style={{ left: frameDraft.x, top: frameDraft.y, width: frameDraft.width, height: frameDraft.height }} />}
                {frames.map(frame => (
                  <div className="ruse-frame" key={frame.id} style={{ left: frame.x, top: frame.y, width: frame.width, height: frame.height } }>
                    <div className="ruse-frame-header">
                      <span
                        className="ruse-frame-title"
                        title={t('Double-click to rename')}
                        onDoubleClick={event => { event.stopPropagation(); setFrameRename(frame); }}
                      >{frame.name || t('Group')}</span>
                      <button
                        className="ruse-frame-remove"
                        title={t('Remove group')}
                        aria-label={t('Remove {name}', { name: frame.name })}
                        onPointerDown={event => event.stopPropagation()}
                        onClick={() => requestRemoveFrame(frame)}
                      >×</button>
                    </div>
                    {FRAME_HANDLES.map(handle => (
                      <div
                        key={handle}
                        className={`ruse-frame-handle ${handle}`}
                        onPointerDown={event => resizeFrame(event, frame, handle)}
                      />
                    ))}
                  </div>
                ))}
              </div>
            </ViewportPortal>            <Panel position="top-left" className="graph-caption"><span>{t('Routing canvas')}</span><strong>{t('{nodes} nodes · {cables} cables', { nodes: nodes.length, cables: edges.length })}</strong></Panel>
            <Panel position="bottom-left" className="canvas-controls">
              <button title={t('Zoom out')} aria-label={t('Zoom out')} onClick={() => flow.zoomOut({ duration: 120 }) }><Minus size={14} /></button>
              <button title={t('Reset zoom')} aria-label={t('Reset zoom')} onClick={() => flow.zoomTo(1, { duration: 160 }) }>{Math.round(zoom * 100)}%</button>
              <button title={t('Zoom in')} aria-label={t('Zoom in')} onClick={() => flow.zoomIn({ duration: 120 }) }><Plus size={14} /></button>
              <button title={t('Fit graph (F)')} aria-label={t('Fit graph')} onClick={fitGraph }><Maximize2 size={14} /></button>
            </Panel>
            {showMiniMap && <MiniMap
              className="ruse-minimap"
              pannable
              zoomable
              nodeColor={node => (node.data as RuseFlowNode['data']).accent}
              nodeStrokeWidth={2}
              maskColor="rgba(7, 10, 14, 0.76)"
            />}
          </ReactFlow>
          {nodes.length === 0 && <div className="graph-empty">
            <span className="eyebrow">{t('Start with a signal')}</span>
            <h2>{t('Build your first route')}</h2>
            <p>{t('Add a source and an output, then drag from OUT to IN. Processing nodes sit between them.')}</p>
            <div className="empty-steps"><span><b>1</b> {t('Source')}</span><span><b>2</b> {t('Processing')}</span><span><b>3</b> {t('Destination')}</span></div>
            <button className="primary-button" onClick={() => quickAdd(systemItem) }>{t('Add system audio')}</button>
            <small>{t('Or drag anything from the library onto the canvas.')}</small>
          </div>}
        </main>
      </div>
      <StatusBar status={status} />
      {showSettings && <SettingsDialog settings={settings} initialTab={settingsTab} outputs={[...catalog.outputs, ...catalog.virtuals]} onChange={next => { setSettings(next); send('settings', { ...next }); }} onClose={() => setShowSettings(false)} />}
      {showClear && <ConfirmDialog title={t('Clear routing graph')} body={t('This removes every node and cable. The current session will be replaced when you close Ruse.')} confirmLabel={t('Clear graph')} onConfirm={() => { setShowClear(false); send('clear'); }} onClose={() => setShowClear(false)} />}
      {recorderRemoval && <ConfirmDialog
        title={t('Recording in progress')}
        body={t('Removing “{name}” will stop and save the current recording. Continue?', { name: recorderRemoval.title })}
        confirmLabel={t('Remove and save')}
        onConfirm={() => {
          if (recorderRemoval.resolve) recorderRemoval.resolve(true);
          else send('remove', { id: recorderRemoval.id });
          setRecorderRemoval(null);
        }}
        onClose={() => {
          recorderRemoval.resolve?.(false);
          setRecorderRemoval(null);
        }}
      />}      {frameRename && <PromptDialog
        title={t('Rename group')}
        label={t('Group name')}
        value={frameRename.name}
        submitLabel={t('Rename')}
        onSubmit={value => {
          saveFrames(framesRef.current.map(frame => frame.id === frameRename.id ? { ...frame, name: value } : frame));
          setFrameRename(null);
        }}
        onClose={() => setFrameRename(null)}
      />}
      {frameRemoval && <RemoveFrameDialog
        frameName={frameRemoval.frame.name}
        nodeCount={frameRemoval.nodeIds.length}
        onRemoveFrame={() => { removeFrameOnly(frameRemoval.frame); setFrameRemoval(null); }}
        onRemoveWithNodes={() => {
          const ids = frameRemoval.nodeIds;
          removeFrameOnly(frameRemoval.frame);
          for (const id of ids) send('remove', { id });
          setFrameRemoval(null);
        }}
        onClose={() => setFrameRemoval(null)}
      />}      {renamePrompt && <PromptDialog title={t('Rename node')} label={t('Node name')} value={renamePrompt.title} submitLabel={t('Rename')} onSubmit={value => { send('rename', { id: renamePrompt.id, title: value }); setRenamePrompt(null); }} onClose={() => setRenamePrompt(null)} />}
      {renameLibrary && <PromptDialog
        title={t('Rename library item')}
        label={t('Display name')}
        value={aliases[catalogKey(renameLibrary)] ?? renameLibrary.name}
        submitLabel={t('Rename')}
        onSubmit={value => {
          setAliases(current => ({ ...current, [catalogKey(renameLibrary)]: value }));
          setRenameLibrary(null);
        }}
        onClose={() => setRenameLibrary(null)}
      />}
      {chainEditor && <ChainEditorDialog
        request={chainEditor}
        effects={catalog.effects.filter(item => item.id !== 'transcriber' && item.id !== 'waveform')}
        onSave={request => {
          send('chain-save', {
            name: request.name,
            previousName: request.previousName ?? '',
            nodeId: request.nodeId ?? '',
            elements: request.elements.map(element => ({ typeId: element.typeId, parameters: element.parameters })),
          });
          setChainEditor(null);
        }}
        onDelete={name => {
          send('chain-delete', { name });
          setChainEditor(null);
        }}
        onClose={() => setChainEditor(null)}
      />}
      {canvasMenu && <div className="context-layer" onPointerDown={event => { if (event.target === event.currentTarget) setCanvasMenu(null); } }>
        <div className="context-menu" role="menu" style={{ left: menuLeft, top: menuTop } }>
          {canvasMenu.kind === 'node' && menuNode && <>
            <button onClick={() => { setRenamePrompt({ id: menuNode.id, title: menuNode.data.title }); setCanvasMenu(null); } }><Pencil size={14} /> {t('Rename')}</button>
            <button onClick={() => { send('duplicate', { id: menuNode.id }); setCanvasMenu(null); } }><Copy size={14} /> {t('Duplicate')} <kbd>Ctrl D</kbd></button>
            <button onClick={() => { send('mute', { id: menuNode.id, value: !menuNode.data.muted }); setCanvasMenu(null); } }>{menuNode.data.muted ? <Volume2 size={14} /> : <VolumeX size={14} />} {menuNode.data.muted ? t('Unmute node') : t('Mute node')} <kbd>M</kbd></button>
            {menuNode.data.isDbMeter && <button onClick={() => { send('db-meter-orientation', { id: menuNode.id, vertical: !menuDbVertical }); setCanvasMenu(null); } }><ArrowUpDown size={14} /> {menuDbVertical ? t('Make horizontal') : t('Make vertical')}</button>}
            <button onClick={() => { send('disconnect-node', { id: menuNode.id }); setCanvasMenu(null); } }><Unplug size={14} /> {t('Disconnect routes')}</button>
            <div className="menu-separator" />
            <button className="danger" onClick={() => { send('remove', { id: menuNode.id }); setCanvasMenu(null); } }><Trash2 size={14} /> {t('Remove node')} <kbd>Del</kbd></button>
          </>}
          {canvasMenu.kind === 'edge' && menuLink && <>
            <button className="danger" onClick={() => { send('disconnect', { id: menuLink.id }); setCanvasMenu(null); } }><Unplug size={14} /> {t('Disconnect cable')}</button>
          </>}
          {canvasMenu.kind === 'pane' && <>
            <button onClick={() => {
              if (canvasMenu.flowX != null && canvasMenu.flowY != null) sendAdd(systemItem, findFreeSpot({ x: canvasMenu.flowX, y: canvasMenu.flowY }));
              setCanvasMenu(null);
            } }><Plus size={14} /> {t('Add system audio')}</button>
            <button onClick={() => { fitGraph(); setCanvasMenu(null); } }><Maximize2 size={14} /> {t('Fit graph')} <kbd>F</kbd></button>
            <button onClick={tidyGraph }><LayoutGrid size={14} /> {t('Tidy up')}</button>
            <div className="menu-separator" />
            <button className="danger" onClick={() => { setShowClear(true); setCanvasMenu(null); } }><Trash2 size={14} /> {t('Clear graph')}</button>
          </>}
        </div>
      </div>}
      {splashOpen && <WelcomeSplash
        settings={settings}
        onChange={next => { setSettings(next); send('settings', { ...next }); }}
        onStartSetup={() => { setSplashOpen(false); setWelcomeOpen(true); }}
        onClose={() => setSplashOpen(false)}
      />}
      {welcomeOpen && <WelcomeWizard
        catalog={catalog}
        onClose={() => { setWelcomeOpen(false); send('onboarding-complete'); }}
        onOpenHelp={() => setShowAudioHelp(true)}
      />}
      {showAudioHelp && <DuplicateAudioDialog warning={primaryWarning} onClose={() => setShowAudioHelp(false)} />}
      {showDeviceHelp && <DeviceHelpDialog onClose={() => setShowDeviceHelp(false)} />}
    </div>
    </I18nProvider>
  );
}
