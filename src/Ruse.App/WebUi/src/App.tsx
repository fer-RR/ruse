import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  Background, BackgroundVariant, MiniMap, Panel, ReactFlow, ViewportPortal,
  useNodesState, useReactFlow, useStore,
} from '@xyflow/react';
import type { Connection, Edge, EdgeChange, NodeChange } from '@xyflow/react';
import {
  AlertTriangle, ArrowUpDown, AudioLines, ChevronLeft, ChevronRight, Copy, Info, LayoutGrid, Maximize2,
  LayoutTemplate, Minus, Pencil, Plus, Snowflake, Trash2, Unplug, Volume2, VolumeX, X, Zap,
} from 'lucide-react';
import { isHosted, send, subscribe } from './bridge';
import { createDemoTelemetry, demoCatalog, demoGraph, demoSettings } from './demo';
import { I18nProvider, normalizeLanguage, translate } from './i18n';
import type { RuseFlowNode } from './flow';
import { chainLayout, nearestFreePosition } from './layout';
import { catalogKey } from './library';
import { SHORTCUTS, hasOpenDialog, shouldIgnoreShortcut } from './shortcuts';
import { getNodeTelemetry, publishTelemetry, setNodeTitles, useUiTelemetry } from './telemetry';
import { setMotionMode, useReducedMotion } from './useReducedMotion';
import RuseNode from './components/RuseNode';
import RuseEdge from './components/RuseEdge';
import RuseConnectionLine from './components/RuseConnectionLine';
import RouteHintOverlay from './components/RouteHintOverlay';
import Sidebar from './components/Sidebar';
import { StatusBar, TitleBar, Toolbar } from './components/Chrome';
import { ConfirmDialog, DuplicateAudioDialog, PromptDialog, RemoveFrameDialog, SettingsDialog, TrayPromptDialog } from './components/Dialogs';
import type { SettingsTab } from './components/Dialogs';
import ChainEditorDialog from './components/ChainEditorDialog';
import type { ExplainerId } from './components/Explainers';
import MixerPanel from './components/MixerPanel';
import TimelineBoundary from './components/TimelineBoundary';
import { DIAGNOSTICS_NODE_ID, loadDiagnosticsLayout, saveDiagnosticsLayout } from './diagnostics';
import type {
  CatalogItem, CatalogSnapshot, ChainEditorRequest, FileAnalysis, FirstRouteResult, FrameModel, GraphSnapshot, HistorySnapshot,
  LinkStateUpdate, MediaListModel, MixerInputStateUpdate, MixerStateUpdate, NodeModel, NodeStateUpdate, SettingsModel, StatusMessage,
  TelemetrySnapshot,
} from './types';
import {
  applyLinkState, applyMixerInputStateToFlow, applyMixerInputStateToGraph,
  applyMixerStateToFlow, applyMixerStateToGraph, applyNodeStateToFlow,
  applyNodeStateToGraph, applyParameterUpdate, applySourceState,
} from './bridgeUpdates';
import type { TimelineAutomationTarget, TimelineState } from './timeline';

const TimelinePanel = lazy(() => import('./components/TimelinePanel'));
const ExplainerDialog = lazy(() => import('./components/Explainers'));
const WelcomeWizard = lazy(() => import('./components/WelcomeWizard'));
const FirstRouteDialog = lazy(() => import('./components/FirstRouteDialog'));
const RouteCheckDialog = lazy(() => import('./components/RouteCheckDialog'));
const ProcessPickerDialog = lazy(() => import('./components/ProcessPickerDialog'));
const nodeTypes = { ruseNode: RuseNode };
const edgeTypes = { ruseEdge: RuseEdge };
const FRAME_HANDLES = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'] as const;
type FrameHandle = typeof FRAME_HANDLES[number];
const emptyCatalog: CatalogSnapshot = {
  apps: [], outputs: [], inputs: [], virtuals: [], effects: [], chains: [], vst3Plugins: [], frameTemplates: [], samples: [],
  defaultOutputId: '', defaultOutputName: '', perAppCaptureAvailable: true,
};
const initialSettings: SettingsModel = demoSettings;
const EMPTY_NODES: NodeModel[] = [];
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
  const [media, setMedia] = useState<MediaListModel>({ folders: [], files: [] });
  const [mediaWaveforms, setMediaWaveforms] = useState<Record<string, number[]>>({});
  const [graph, setGraph] = useState<GraphSnapshot>(isHosted ? { version: 0, nodes: [], links: [], running: false } : demoGraph);
  const [settings, setSettings] = useState<SettingsModel>(initialSettings);
  const language = normalizeLanguage(settings.uiLanguage);
  const theme = settings.theme;
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  const t = useMemo(() => (text: string, vars?: Record<string, string | number>) => translate(language, text, vars), [language]);
  const ui = useUiTelemetry();
  const running = ui.running || graph.running;
  const [status, setStatus] = useState<StatusMessage>({ text: isHosted ? 'Starting native audio engine.' : 'Interface preview - native engine not attached.', isError: false });
  const [startup, setStartup] = useState({ visible: isHosted, progress: 0.08 });
  const [alert, setAlert] = useState<StatusMessage | null>(null);
  const [toast, setToast] = useState<StatusMessage | null>(null);
  useEffect(() => {
    if (!toast) return;
    const timer = window.setTimeout(() => setToast(null), 4500);
    return () => window.clearTimeout(timer);
  }, [toast]);
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
  const [mixerOpen, setMixerOpen] = useState(() => localStorage.getItem('ruse.mixerOpen') === '1');
  const [explainer, setExplainer] = useState<ExplainerId | null>(null);
  const explainerUsage = useRef({ mixer: false, sidechain: false, vst3: false, native: false });
  const [timelineOpen, setTimelineOpen] = useState(false);
  useEffect(() => {
    activeSurfaceRef.current = timelineOpen ? 'timeline' : 'graph';
  }, [timelineOpen]);
  const [timelineIntroVisible, setTimelineIntroVisible] = useState(false);
  const dismissTimelineIntro = useCallback(() => setTimelineIntroVisible(false), []);
  const [timeline, setTimeline] = useState<TimelineState>({
    project: {
      version: 1,
      sampleRate: 48000,
      tempo: 120,
      timeSignatureNumerator: 4,
      timeSignatureDenominator: 4,
      loopStartSample: -1,
      loopEndSample: -1,
      markers: [],
      tracks: [],
    },
    playheadSample: 0,
    playing: false,
    sampleRate: 48000,
    canUndo: false,
    canRedo: false,
    recording: false,
    metronome: false,
    countInBeats: 4,
  });
  const [mixerWidth, setMixerWidth] = useState(() => {
    const saved = Number(localStorage.getItem('ruse.mixerWidth'));
    return saved >= 240 && saved <= 520 ? saved : 320;
  });
  const [showSettings, setShowSettings] = useState(false);
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('behavior');
  const [showAudioHelp, setShowAudioHelp] = useState(false);
  const [showRouteCheck, setShowRouteCheck] = useState(false);
  const [processPicker, setProcessPicker] = useState<{ id: string; title: string; processId?: number; processName?: string } | null>(null);
  const [firstRouteOpen, setFirstRouteOpen] = useState(false);
  const [firstRouteResult, setFirstRouteResult] = useState<FirstRouteResult | null>(null);
  const [welcomeOpen, setWelcomeOpen] = useState(false);
  const [showClear, setShowClear] = useState(false);
  const [timelineTrackRemoval, setTimelineTrackRemoval] = useState<{ id: string; name: string } | null>(null);
  const [timelineTrackRename, setTimelineTrackRename] = useState<{ id: string; name: string } | null>(null);
  const [trayPrompt, setTrayPrompt] = useState(false);
  const [renamePrompt, setRenamePrompt] = useState<RenamePrompt | null>(null);
  const [canvasMenu, setCanvasMenu] = useState<CanvasMenu | null>(null);
  const [chainEditor, setChainEditor] = useState<ChainEditorRequest | null>(null);
  const [renameLibrary, setRenameLibrary] = useState<CatalogItem | null>(null);
  const [libraryRemoval, setLibraryRemoval] = useState<CatalogItem | null>(null);
  const [aliases, setAliases] = useState<Record<string, string>>(() => {
    try {
      const parsed: unknown = JSON.parse(localStorage.getItem('ruse.libraryAliases') ?? '{}');
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
      return Object.fromEntries(
        Object.entries(parsed as Record<string, unknown>)
          .filter((entry): entry is [string, string] => typeof entry[1] === 'string'));
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
  const [frameTipDismissed, setFrameTipDismissed] = useState(() => localStorage.getItem('ruse.frameTipDismissed') === '1');
  const [selectedEdgeIds, setSelectedEdgeIds] = useState<Set<string>>(() => new Set());
  const appShellRef = useRef<HTMLDivElement>(null);
  const activeSurfaceRef = useRef<'graph' | 'timeline'>('graph');
  const connectingRef = useRef(false);
  const draggingRef = useRef(false);
  const pendingTelemetry = useRef<TelemetrySnapshot | null>(null);
  const pendingMoves = useRef<Map<string, { x: number; y: number; at: number  }>>(new Map());
  const framesRef = useRef<FrameModel[]>([]);
  const frameMoveCleanup = useRef<(() => void) | null>(null);
  const frameEditing = useRef(false);
  const graphSeen = useRef(false);
  const [rejectedLinkId, setRejectedLinkId] = useState<string | null>(null);

  const nodesRef = useRef(nodes);
  const graphRef = useRef(graph);
  const settingsRef = useRef(settings);
  const sidebarCollapsedRef = useRef(sidebarCollapsed);
  const zCounter = useRef(1);
  useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  useEffect(() => { graphRef.current = graph; }, [graph]);
  useEffect(() => { framesRef.current = frames; }, [frames]);
  useEffect(() => { settingsRef.current = settings; }, [settings]);
  useEffect(() => {
    setMotionMode(settings.motionMode ?? 'system');
  }, [settings.motionMode]);
  useEffect(() => () => frameMoveCleanup.current?.(), []);
  const reducedMotion = useReducedMotion();
  const applyDiagnostics = useCallback((show: boolean) => {
    const next = { ...settingsRef.current, showDiagnostics: show };
    settingsRef.current = next;
    setSettings(next);
    send('settings', next);
  }, []);
  const toggleDiagnostics = useCallback(() => {
    applyDiagnostics(!settingsRef.current.showDiagnostics);
  }, [applyDiagnostics]);
  const closeMixer = useCallback(() => setMixerOpen(false), []);
  const openFirstRoute = useCallback(() => {
    setFirstRouteResult(null);
    setFirstRouteOpen(true);
  }, []);
  const openExplainer = useCallback((id: ExplainerId) => {
    localStorage.setItem(`ruse.explainer.${id}`, '1');
    setExplainer(id);
  }, []);
  useEffect(() => {
    const onPickProcess = (event: Event) => setProcessPicker((event as CustomEvent).detail);
    window.addEventListener('ruse-pick-process', onPickProcess);
    return () => window.removeEventListener('ruse-pick-process', onPickProcess);
  }, []);
  useEffect(() => {
    const onOpenExplainer = (event: Event) => {
      const id = (event as CustomEvent).detail;
      if (id === 'mixer' || id === 'sidechain' || id === 'vst3' || id === 'native') openExplainer(id);
    };
    window.addEventListener('ruse-open-explainer', onOpenExplainer);
    return () => window.removeEventListener('ruse-open-explainer', onOpenExplainer);
  }, [openExplainer]);
  useEffect(() => {
    if (explainer || startup.visible || welcomeOpen) return;
    const current = {
      mixer: mixerOpen,
      sidechain: nodes.some(node => node.data.supportsSidechain),
      vst3: nodes.some(node => node.data.isVst3),
      native: graph.links.some(link => link.native === true),
    };
    const next = (Object.keys(current) as ExplainerId[]).find(id =>
      current[id] && !explainerUsage.current[id] &&
      localStorage.getItem(`ruse.explainer.${id}`) !== '1');
    explainerUsage.current = current;
    if (next) openExplainer(next);
  }, [explainer, graph.links, mixerOpen, nodes, openExplainer, startup.visible, welcomeOpen]);
  useEffect(() => { sidebarCollapsedRef.current = sidebarCollapsed; }, [sidebarCollapsed]);
  useEffect(() => { localStorage.setItem('ruse.sidebarWidth', String(sidebarWidth)); }, [sidebarWidth]);
  useEffect(() => { localStorage.setItem('ruse.sidebarCollapsed', sidebarCollapsed ? '1' : '0'); }, [sidebarCollapsed]);
  useEffect(() => {
    if (!timelineOpen) {
      setTimelineIntroVisible(false);
      return;
    }

    if (localStorage.getItem('ruse.timelineIntroSeen') === '1') return;
    localStorage.setItem('ruse.timelineIntroSeen', '1');
    setTimelineIntroVisible(true);
  }, [timelineOpen]);

  useEffect(() => { localStorage.setItem('ruse.mixerOpen', mixerOpen ? '1' : '0'); }, [mixerOpen]);
  useEffect(() => {
    localStorage.setItem('ruse.mixerWidth', String(mixerWidth));
    workspaceRef.current?.style.setProperty('--mixer-width', `${mixerWidth}px`);
  }, [mixerWidth]);
  useLayoutEffect(() => {
    workspaceRef.current?.style.setProperty('--sidebar-width', `${sidebarWidth}px`);
  }, [sidebarWidth]);
  useEffect(() => { localStorage.setItem('ruse.libraryAliases', JSON.stringify(aliases)); }, [aliases]);

  useEffect(() => { localStorage.setItem('ruse.frameTipDismissed', frameTipDismissed ? '1' : '0'); }, [frameTipDismissed]);
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

  const moveFrame = (event: React.PointerEvent<HTMLDivElement>, frame: FrameModel) => {
    if (event.button !== 0 || event.altKey) return;
    const target = event.target as Element | null;
    if (target?.closest('button, input, select, textarea')) return;
    event.preventDefault();
    event.stopPropagation();
    frameMoveCleanup.current?.();
    frameEditing.current = true;
    draggingRef.current = true;
    setInteracting(true);

    const origin = { ...frame };
    const start = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
    const movingNodes = nodesRef.current.filter(node => {
      const width = node.measured?.width ?? node.data.width ?? 204;
      const height = node.measured?.height ?? 140;
      const centerX = node.position.x + width / 2;
      const centerY = node.position.y + height / 2;
      return centerX >= origin.x && centerX <= origin.x + origin.width &&
        centerY >= origin.y && centerY <= origin.y + origin.height;
    });
    const origins = new Map(movingNodes.map(node => [node.id, { ...node.position }]));
    let next = origin;
    let moved = false;

    const move = (moveEvent: PointerEvent) => {
      const point = flow.screenToFlowPosition({ x: moveEvent.clientX, y: moveEvent.clientY });
      const x = snapFrameValue(origin.x + point.x - start.x);
      const y = snapFrameValue(origin.y + point.y - start.y);
      const dx = x - origin.x;
      const dy = y - origin.y;
      if (dx === 0 && dy === 0 && !moved) return;
      moved = true;
      next = { ...origin, x, y };
      framesRef.current = framesRef.current.map(item => item.id === frame.id ? next : item);
      setFrames(framesRef.current);
      if (origins.size > 0) {
        setNodes(current => current.map(node => {
          const from = origins.get(node.id);
          return from ? { ...node, position: { x: from.x + dx, y: from.y + dy } } : node;
        }));
      }
    };

    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      frameMoveCleanup.current = null;
      frameEditing.current = false;
      draggingRef.current = false;
      setInteracting(false);
      flushPendingTelemetry();
      if (!moved) return;
      saveFrames(framesRef.current);
      const dx = next.x - origin.x;
      const dy = next.y - origin.y;
      for (const node of movingNodes) {
        const from = origins.get(node.id);
        if (!from) continue;
        const x = from.x + dx;
        const y = from.y + dy;
        if (node.data.diagnostics) {
          saveDiagnosticsLayout({ ...loadDiagnosticsLayout(), x, y });
        } else {
          pendingMoves.current.set(node.id, { x, y, at: performance.now() });
          send('move', { id: node.id, x, y });
        }
      }
    };

    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
    frameMoveCleanup.current = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
    };
  };

  useEffect(() => {
    const ids = new Set(graph.links.map(link => link.id));
    setSelectedEdgeIds(current => {
      if ([...current].every(id => ids.has(id))) return current;
      return new Set([...current].filter(id => ids.has(id)));
    });
  }, [graph.links]);

  const removeFrameOnly = (frame: FrameModel) => {
    saveFrames(framesRef.current.filter(item => item.id !== frame.id));
  };

  const confirmRecorderRemoval = (ids: string[]): Promise<boolean> => {
    const active = nodesRef.current.filter(node =>
      ids.includes(node.id) &&
      node.data.isRecorder &&
      getNodeTelemetry(node.id)?.recordingStatus === 'REC');
    if (active.length === 0) return Promise.resolve(true);
    return new Promise(resolve => {
      setRecorderRemoval({
        id: active[0].id,
        title: active.map(node => node.data.title).join(', '),
        resolve,
      });
    });
  };

  const requestClear = async () => {
    const recorderIds = nodesRef.current
      .filter(node => !node.data.diagnostics && node.data.isRecorder)
      .map(node => node.id);
    if (await confirmRecorderRemoval(recorderIds)) setShowClear(true);
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
      case 'media-list': setMedia(payload as MediaListModel); break;
      case 'media-waveform': { const update = payload as { path: string; peaks: number[] }; setMediaWaveforms(current => ({ ...current, [update.path]: update.peaks })); break; }
      case 'graph': setGraph(payload as GraphSnapshot); break;
      case 'node-state': {
        const update = payload as NodeStateUpdate;
        setGraph(current => applyNodeStateToGraph(current, update));
        setNodes(current => applyNodeStateToFlow(current, update));
        break;
      }
      case 'mixer-state': {
        const update = payload as MixerStateUpdate;
        setGraph(current => applyMixerStateToGraph(current, update));
        setNodes(current => applyMixerStateToFlow(current, update));
        break;
      }
      case 'mixer-input-state': {
        const update = payload as MixerInputStateUpdate;
        setGraph(current => applyMixerInputStateToGraph(current, update));
        setNodes(current => applyMixerInputStateToFlow(current, update));
        break;
      }
      case 'link-state': {
        const update = payload as LinkStateUpdate;
        setGraph(current => applyLinkState(current, update));
        break;
      }
      case 'link-reject': {
        const reject = payload as { id?: string; reason?: string };
        if (reject.id) {
          setRejectedLinkId(reject.id);
          window.setTimeout(() => {
            setRejectedLinkId(current => current === reject.id ? null : current);
          }, 900);
        }
        break;
      }
      case 'param': {
        const update = payload as { id: string; processor: number; parameter: number; value: number };
        setNodes(current => applyParameterUpdate(current, update));
        break;
      }
      case 'source-state': {
        const update = payload as {
          id: string;
          toneFrequency?: number; toneWaveform?: string; toneLevelDb?: number;
          metronomeBpm?: number; metronomeBeatsPerBar?: number;
          synthFrequency?: number; synthWaveform?: string; synthLevelDb?: number;
          synthHarmonics?: number; synthDetuneCents?: number; synthNoise?: number;
          synthCutoffHz?: number; synthResonance?: number; synthMovement?: number;
          synthLfoRateHz?: number; synthStereoWidth?: number;
        };
        setNodes(current => applySourceState(current, update));
        break;
      }
      case 'timeline-bounce-progress': {
        const update = payload as { active?: boolean; progress?: number };
        setTimeline(current => ({ ...current, bouncing: update.active === true, bounceProgress: update.progress ?? 0 }));
        break;
      }
      case 'telemetry':
        if (connectingRef.current || draggingRef.current) {
          pendingTelemetry.current = payload as TelemetrySnapshot;
        } else {
          publishTelemetry(payload as TelemetrySnapshot);
        }
        break;
      case 'settings': setSettings(payload as SettingsModel); break;
      case 'file-analysis': {
        const update = payload as { id: string; analysis?: FileAnalysis; error?: string };
        setNodes(current => current.map(node => node.id === update.id
          ? {
              ...node,
              data: {
                ...node.data,
                fileAnalysis: update.analysis ?? null,
                fileAnalysisError: update.error,
              },
            }
          : node));
        break;
      }
      case 'first-route': setFirstRouteResult(payload as FirstRouteResult); break;
      case 'tray-open-mixer': setMixerOpen(true); break;
      case 'tray-open-timeline': setTimelineOpen(true); break;
      case 'timeline': {
        const incoming = payload as TimelineState;
        setTimeline(current => {
          const peaks = new Map(current.project.tracks.flatMap(track =>
            track.clips.map(clip => [clip.id, clip.peaks] as const)));
          const tracks = incoming.project.tracks.map(track => ({
            ...track,
            clips: track.clips.map(clip => ({
              ...clip,
              peaks: clip.peaks?.length ? clip.peaks : peaks.get(clip.id) ?? [],
            })),
          }));
          return { ...incoming, project: { ...incoming.project, tracks } };
        });
        break;
      }
      case 'timeline-playhead': {
        const update = payload as { playheadSample: number; playing: boolean };
        setTimeline(current => current.playheadSample === update.playheadSample && current.playing === update.playing
          ? current
          : { ...current, playheadSample: update.playheadSample, playing: update.playing });
        break;
      }
      case 'history': setHistory(payload as HistorySnapshot); break;
      case 'chain-edit': setChainEditor(payload as ChainEditorRequest); break;
      case 'status': setStatus(payload as StatusMessage); break;
      case 'alert': setAlert(payload as StatusMessage); break;
      case 'toast': setToast(payload as StatusMessage); break;
      case 'splash':
      case 'welcome': setWelcomeOpen(true); break;
      case 'startup-progress': {
        const detail = payload as { progress?: number };
        const progress = Number.isFinite(detail.progress) ? Math.max(0.05, Math.min(1, detail.progress!)) : 0.1;
        setStartup(current => ({ ...current, visible: true, progress }));
        break;
      }
      case 'startup-complete':
        setStartup(current => ({ ...current, progress: 1 }));
        window.setTimeout(() => setStartup(current => ({ ...current, visible: false })), 250);
        break;
      case 'close-to-tray': setTrayPrompt(true); break;
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
    const fileOutputs = graph.nodes
      .filter(node => node.kind === 'output' && !node.isRecorder)
      .map(node => ({ id: node.id, name: node.title }));
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
          fileOutputs,
          showDiagnostics: settingsRef.current.showDiagnostics,
          midiEnabled: settingsRef.current.midiEnabled === true,
          highlightMuted: settingsRef.current.highlightMutedNodes,
          transcriberModels: settingsRef.current.transcriberModels,
          translatorModels: settingsRef.current.translatorModels,
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
      const flagged = current.map(node => node.data.showDiagnostics === settings.showDiagnostics
        ? node
        : { ...node, data: { ...node.data, showDiagnostics: settings.showDiagnostics } });
      const exists = flagged.some(node => node.id === DIAGNOSTICS_NODE_ID);
      if (settings.showDiagnostics && !exists) {
        return [...flagged, { ...createDiagnosticsNode(), zIndex: ++zCounter.current }];
      }
      if (!settings.showDiagnostics && exists) {
        return flagged.filter(node => node.id !== DIAGNOSTICS_NODE_ID);
      }
      return flagged;
    });
  }, [settings.showDiagnostics, setNodes]);

  useEffect(() => {
    setNodes(current => current.map(node => node.data.isTranscriber
      ? { ...node, data: { ...node.data, transcriberModels: settings.transcriberModels } }
      : node));
  }, [settings.transcriberModels, setNodes]);

  useEffect(() => {
    setNodes(current => current.map(node => node.data.isTranslator
      ? { ...node, data: { ...node.data, translatorModels: settings.translatorModels } }
      : node));
  }, [settings.translatorModels, setNodes]);

  useEffect(() => {
    setNodes(current => current.map(node => node.data.midiEnabled === (settings.midiEnabled === true)
      ? node
      : { ...node, data: { ...node.data, midiEnabled: settings.midiEnabled === true } }));
  }, [settings.midiEnabled, setNodes]);

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
    const closeDiagnostics = () => applyDiagnostics(false);
    window.addEventListener('ruse-close-diagnostics', closeDiagnostics);
    return () => window.removeEventListener('ruse-close-diagnostics', closeDiagnostics);
  }, [applyDiagnostics]);

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

  const edges = useMemo<Edge[]>(() => {
    const byId = new Map(graph.nodes.map(node => [node.id, node]));
    return graph.links.map(link => {
      const source = byId.get(link.from);
      return {
        id: link.id,
        source: link.from,
        target: link.to,
        sourceHandle: source?.isNoteDetector ? 'key' : 'out',
        targetHandle: link.kind === 'barge-key' ? 'barge' : link.kind === 'sidechain' ? 'sidechain' : 'in',
        type: 'ruseEdge',
        data: { kind: link.kind, native: link.native === true, nativeRate: link.native === true ? (link.nativeRate || source?.sampleRate) : undefined, rejected: rejectedLinkId === link.id, physics: settings.cablePhysics },
        animated: false,
        selected: selectedEdgeIds.has(link.id),
        interactionWidth: 22,
        style: { stroke: source?.accent ?? '#5B8DEF', strokeWidth: 2, opacity: 0.84 },
      };
    });
  }, [graph.links, graph.nodes, rejectedLinkId, settings.cablePhysics, selectedEdgeIds]);

  const sendAdd = useCallback((item: CatalogItem, position: { x: number; y: number }, linkId?: string, connectTo?: string) => {
    send('add', {
      kind: item.kind,
      id: item.id,
      title: aliases[catalogKey(item)]?.trim() || t(item.name),
      processId: item.processId ?? 0,
      micHint: item.micHint ?? '',
      filePath: item.filePath ?? '',
      linkId: linkId ?? '',
      x: Math.round(position.x / 20) * 20,
      y: Math.round(position.y / 20) * 20,
      connectTo: connectTo ?? '',
    });
  }, [aliases, t]);

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

  const focusMixerNode = useCallback((id: string) => {
    const target = nodesRef.current.find(node => node.id === id);
    if (!target) return;

    const currentTop = nodesRef.current.reduce((top, node) => Math.max(top, node.zIndex ?? 0), 0);
    zCounter.current = Math.max(zCounter.current, currentTop) + 1;
    const zIndex = zCounter.current;
    setNodes(current => current.map(node => {
      if (node.id === id) return { ...node, zIndex, selected: true };
      return node.selected ? { ...node, selected: false } : node;
    }));

    const width = target.measured?.width ?? 204;
    const height = target.measured?.height ?? 140;
    flow.setCenter(target.position.x + width / 2, target.position.y + height / 2, {
      zoom: flow.getZoom(),
      duration: reducedMotion ? 0 : 220,
    });
  }, [flow, reducedMotion, setNodes]);

  const findFreeSpot = useCallback((position: { x: number; y: number }, width = 204, height = 140) => {
    const others = nodesRef.current.map(node => ({
      id: node.id,
      x: node.position.x,
      y: node.position.y,
      width: node.measured?.width ?? 204,
      height: node.measured?.height ?? 140,
    }));
    return nearestFreePosition({ id: 'new', x: position.x, y: position.y, width, height }, others);
  }, []);

  const quickAdd = useCallback((item: CatalogItem) => {
    const bounds = graphElement.current?.getBoundingClientRect();
    const count = nodesRef.current.length;
    const position = bounds
      ? flow.screenToFlowPosition({ x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 })
      : { x: 60 + count * 30, y: 60 + count * 30 };
    if (item.kind === 'template') {
      send('insert-template', {
        id: item.id,
        x: Math.round((position.x - 180) / 20) * 20,
        y: Math.round((position.y - 120) / 20) * 20,
      });
      return;
    }

    const width = item.kind === 'vst3' ? 340 : 204;
    sendAdd(item, findFreeSpot({ x: position.x - width / 2, y: position.y - 60 }, width));
  }, [flow, sendAdd, findFreeSpot]);

  const newChain = useCallback(
    () => setChainEditor({ name: 'My chain', elements: [] }), []);
  const rescanPlugins = useCallback(() => send('vst3-rescan'), []);
  const editChain = useCallback((item: CatalogItem) => setChainEditor({
    name: item.name,
    previousName: item.name,
    elements: (item.elements ?? []).map(element => ({ typeId: element.typeId, parameters: { ...element.parameters } })),
  }), []);

  const tidyGraph = () => {
    const layoutNodes = nodesRef.current
      .filter(node => !node.data.diagnostics)
      .map(node => ({
        id: node.id,
        kind: node.data.kind,
        width: node.measured?.width ?? 204,
        height: node.measured?.height ?? 140,
      }));
    const positions = chainLayout(
      layoutNodes,
      graphRef.current.links.map(link => ({ from: link.from, to: link.to, kind: link.kind })),
    );

    const byId = new Map(positions.map(position => [position.id, position]));
    setNodes(current => current.map(node => {
      const position = byId.get(node.id);
      return position ? { ...node, position: { x: position.x, y: position.y } } : node;
    }));
    send('move-many', { positions });
    setCanvasMenu(null);
  };


  const openTimeline = useCallback(() => {
    setTimelineOpen(true);
    send('timeline-open');
  }, [send]);

  const closeTimeline = useCallback(() => {
    send('timeline-stop');
    setTimelineOpen(false);
  }, [send]);

  const toggleTimeline = useCallback(() => {
    if (timelineOpen) {
      closeTimeline();
      return;
    }

    openTimeline();
  }, [closeTimeline, openTimeline, timelineOpen]);


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
      send('connect', {
        from: connection.source,
        to: connection.target,
        kind: connection.targetHandle === 'barge'
          ? 'barge-key'
          : connection.targetHandle === 'sidechain' ? 'sidechain' : '',
      });
    }
  };

  const nodeById = useMemo(() => new Map(graph.nodes.map(node => [node.id, node])), [graph.nodes]);
  const isValidConnection = useCallback((connection: Connection | Edge) => {
    const source = connection.source ? nodeById.get(connection.source) : undefined;
    const target = connection.target ? nodeById.get(connection.target) : undefined;
    if (!source || !target || source.id === target.id) return false;
    if (source.isNoteDetector && connection.targetHandle !== 'sidechain') return false;
    if (connection.targetHandle === 'barge') {
      return source.kind === 'modifier' &&
             (source.isTranscriber === true || source.isTranslator === true) &&
             target.isTts === true;
    }
    if (connection.targetHandle === 'sidechain') {
      return (source.kind === 'source' || source.kind === 'modifier') &&
             target.kind === 'modifier' && target.supportsSidechain === true;
    }
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

    const draggedRect = {
      x: node.position.x,
      y: node.position.y,
      width: moving.width,
      height: moving.height,
    };
    let connectTarget: RuseFlowNode | undefined;
    let bestOverlap = 0;
    if (node.data.kind === 'source' || node.data.kind === 'output') {
      for (const candidate of nodesRef.current) {
        if (candidate.id === node.id || candidate.data.diagnostics) continue;
        const matches = node.data.kind === 'source'
          ? candidate.data.kind === 'output'
          : candidate.data.kind === 'source';
        if (!matches) continue;
        const targetRect = {
          x: candidate.position.x,
          y: candidate.position.y,
          width: candidate.measured?.width ?? candidate.data.width ?? 204,
          height: candidate.measured?.height ?? 140,
        };
        const overlapWidth = Math.max(0, Math.min(draggedRect.x + draggedRect.width, targetRect.x + targetRect.width) - Math.max(draggedRect.x, targetRect.x));
        const overlapHeight = Math.max(0, Math.min(draggedRect.y + draggedRect.height, targetRect.y + targetRect.height) - Math.max(draggedRect.y, targetRect.y));
        const overlap = overlapWidth * overlapHeight;
        const threshold = Math.min(draggedRect.width * draggedRect.height, targetRect.width * targetRect.height) * 0.35;
        if (overlap >= threshold && overlap > bestOverlap) {
          bestOverlap = overlap;
          connectTarget = candidate;
        }
      }
    }

    if (connectTarget) {
      const from = node.data.kind === 'source' ? node.id : connectTarget.id;
      const to = node.data.kind === 'source' ? connectTarget.id : node.id;
      const exists = graphRef.current.links.some(link =>
        link.kind === 'direct' && link.from === from && link.to === to);
      if (!exists) {
        send('connect', { from, to, gain: 1, enabled: true });
      }
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
      if (change.type === 'remove') {
        send('disconnect', { id: change.id });
        setSelectedEdgeIds(current => {
          if (!current.has(change.id)) return current;
          const next = new Set(current);
          next.delete(change.id);
          return next;
        });
      } else if (change.type === 'select') {
        setSelectedEdgeIds(current => {
          if (current.has(change.id) === change.selected) return current;
          const next = new Set(current);
          if (change.selected) next.add(change.id);
          else next.delete(change.id);
          return next;
        });
      }
    }
  };

  const fitGraph = () => flow.fitView({ padding: 0.18, duration: reducedMotion ? 0 : 180 });
  const modalOpen = showSettings || showAudioHelp || welcomeOpen || firstRouteOpen || showRouteCheck || processPicker != null || explainer != null ||
    showClear || trayPrompt || renamePrompt != null || chainEditor != null ||
    renameLibrary != null || libraryRemoval != null || canvasMenu != null;

  useEffect(() => {
    if (!canvasMenu) return;
    document.querySelector<HTMLElement>('.context-menu [role="menuitem"]')?.focus();
  }, [canvasMenu]);

  const onContextMenuKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const items = Array.from(
      event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'));
    if (items.length === 0) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      setCanvasMenu(null);
      return;
    }

    const current = items.indexOf(document.activeElement as HTMLButtonElement);
    let next = current;
    if (event.key === 'ArrowDown') next = current < 0 ? 0 : (current + 1) % items.length;
    else if (event.key === 'ArrowUp') next = current < 0 ? items.length - 1 : (current - 1 + items.length) % items.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = items.length - 1;
    else return;
    event.preventDefault();
    items[next].focus();
  };

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      if (event.defaultPrevented) return;
      if (modalOpen || hasOpenDialog()) return;

      const target = event.target as HTMLElement;
      const textInput = target instanceof HTMLInputElement &&
        !['range', 'checkbox', 'radio', 'button', 'submit'].includes(target.type);
      const editing = textInput || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable;
      if (event.key === 'F1') {
        event.preventDefault();
        setSettingsTab('diagnostics');
        setShowSettings(true);
        return;
      }
      if (event.key === 'F5') {
        event.preventDefault();
        send('refresh');
        return;
      }
      if (shouldIgnoreShortcut(event, false)) return;
      if (editing) return;
      const modifier = event.ctrlKey || event.metaKey;
      const timelineHistory = timelineOpen && activeSurfaceRef.current === 'timeline';
      if (modifier && event.key.toLowerCase() === 'z') {
        event.preventDefault();
        send(timelineHistory
          ? (event.shiftKey ? 'timeline-redo' : 'timeline-undo')
          : (event.shiftKey ? 'redo' : 'undo'));
      } else if (modifier && event.key.toLowerCase() === 'y') {
        event.preventDefault();
        send(timelineHistory ? 'timeline-redo' : 'redo');
      } else if (modifier && event.shiftKey && event.key.toLowerCase() === 'd') {
        event.preventDefault();
        toggleDiagnostics();
      } else if (modifier && event.shiftKey && event.key.toLowerCase() === 'm') {
        event.preventDefault();
        send('mic-mute', { value: !ui.micMuted });
      } else if (modifier && event.key === ',') {
        event.preventDefault();
        setSettingsTab('behavior');
        setShowSettings(true);
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
      } else if (modifier && event.key.toLowerCase() === 'l') {
        event.preventDefault();
        setSidebarCollapsed(value => !value);
      } else if (modifier && event.key.toLowerCase() === 'm') {
        event.preventDefault();
        setMixerOpen(value => !value);
      } else if (modifier && event.key.toLowerCase() === 't') {
        event.preventDefault();
        toggleTimeline();
      } else if (modifier && !event.shiftKey && event.key.toLowerCase() === 'r') {
        event.preventDefault();
        send('transport', { running: !running });
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
        setSettingsTab('diagnostics');
        setShowSettings(true);
      } else if (event.key === ' ') {
        event.preventDefault();
        event.stopPropagation();
        if (event.repeat) return;
        if (!timelineOpen) {
          setTimelineOpen(true);
          send('timeline-open');
        }
        send(timeline.playing ? 'timeline-pause' : 'timeline-play');
      } else if (event.key.toLowerCase() === 'b') {
        if (activeSurfaceRef.current !== 'graph') return;
        const selected = nodesRef.current.find(node =>
          node.selected && node.data.kind === 'modifier' && !node.data.diagnostics);
        if (selected) {
          send('effect-enabled', { id: selected.id, value: selected.data.enabled === false });
        }
      } else if (event.key.toLowerCase() === 'f') {
        if (activeSurfaceRef.current !== 'graph') return;
        fitGraph();
      } else if (event.key.toLowerCase() === 'm') {
        if (activeSurfaceRef.current !== 'graph') return;
        muteSelected();
      } else if (event.key.toLowerCase() === 't') {
        if (activeSurfaceRef.current !== 'graph') return;
        tidyGraph();
      } else if (event.key === '0') {
        if (activeSurfaceRef.current !== 'graph') return;
        flow.zoomTo(1, { duration: reducedMotion ? 0 : 160 });
      } else if (event.key === '+' || event.key === '=') {
        if (activeSurfaceRef.current !== 'graph') return;
        flow.zoomIn({ duration: reducedMotion ? 0 : 120 });
      } else if (event.key === '-') {
        if (activeSurfaceRef.current !== 'graph') return;
        flow.zoomOut({ duration: reducedMotion ? 0 : 120 });
      } else if (event.key === 'Escape') {
        setCanvasMenu(null);
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [flow, reducedMotion, toggleDiagnostics, modalOpen, running, ui.micMuted, timeline.playing, timelineOpen, toggleTimeline]);

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

  const startMixerResize = useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    const startX = event.clientX;
    const startWidth = mixerWidth;
    let nextWidth = startWidth;
    document.body.classList.add('mixer-resizing');
    const move = (moveEvent: PointerEvent) => {
      nextWidth = Math.min(520, Math.max(240, startWidth - (moveEvent.clientX - startX)));
      workspaceRef.current?.style.setProperty('--mixer-width', `${nextWidth}px`);
    };
    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      document.body.classList.remove('mixer-resizing');
      setMixerWidth(nextWidth);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
  }, [mixerWidth]);

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
  const graphNodeCount = useMemo(
    () => nodes.filter(node => !(node.data as { diagnostics?: boolean }).diagnostics).length,
    [nodes],
  );
  const showMiniMap = graphNodeCount > 0 && settings.minimapMode !== 1 && anyNodeOffscreen;
  const audioPlaying = catalog.apps.some(item => item.active === true);
  const audibleSourceSignature = useMemo(() => {
    const activeApps = catalog.apps.filter(app => app.active === true);
    return graph.nodes
      .filter(node => {
        if (node.kind !== 'source' || node.muted) return false;
        if (node.flavor === 'system') return activeApps.length > 0;
        if (node.flavor !== 'app') return false;
        return activeApps.some(app =>
          (node.processId != null && app.processId === node.processId) ||
          (!!node.processName && !!app.processName &&
            app.processName.toLowerCase() === node.processName.toLowerCase()));
      })
      .map(node => node.id)
      .sort()
      .join('|');
  }, [catalog.apps, graph.nodes]);

  const audibleSourceSignatureRef = useRef('');
  useEffect(() => {
    if (audibleSourceSignatureRef.current === audibleSourceSignature) return;
    audibleSourceSignatureRef.current = audibleSourceSignature;
    if (audibleSourceSignature && !running) {
      send('transport', { running: true });
    }
  }, [audibleSourceSignature, running]);

  const primaryWarning = graph.warnings?.find(warning => warning.severity === 'warning') ?? graph.warnings?.[0];
  const warningSignature = primaryWarning
    ? `${primaryWarning.severity}|${primaryWarning.sourceId}|${primaryWarning.outputId}|${primaryWarning.deviceName}|${primaryWarning.message}`
    : '';
  useEffect(() => {
    if (!warningSignature) setDismissedWarning('');
  }, [warningSignature]);
  const warningVisible = !!primaryWarning && warningSignature !== dismissedWarning;
  const deviceSignature = useMemo(
    () => graph.nodes.map(node => `${node.id}:${node.deviceId ?? ''}`).join('|'),
    [graph.nodes],
  );
  const nodeStructureSignature = useMemo(
    () => graph.nodes.map(node =>
      `${node.id}:${node.kind}:${node.title}:${node.enabled === false}:${node.isMetronome === true}:${node.isToneGenerator === true}:${node.isSynthGenerator === true}:${node.parameters.map(parameter => `${parameter.processorIndex}.${parameter.parameterIndex}`).join(',')}`).join('|'),
    [graph.nodes],
  );
  const linkStructureSignature = useMemo(
    () => graph.links.map(link => `${link.id}:${link.from}:${link.to}:${link.kind}:${link.enabled !== false}`).join('|'),
    [graph.links],
  );
  const usedDeviceIds = useMemo(
    () => new Set(graph.nodes.filter(node => node.deviceId).map(node => node.deviceId!)),
    [deviceSignature],
  );
  const mixerSources = useMemo(
    () => mixerOpen ? graph.nodes.filter(node => node.kind === 'source') : EMPTY_NODES,
    [graph.nodes, mixerOpen],
  );
  const mixerOutputs = useMemo(
    () => mixerOpen ? graph.nodes.filter(node => node.kind === 'output') : EMPTY_NODES,
    [graph.nodes, mixerOpen],
  );
  const mixerModifiers = useMemo(
    () => mixerOpen ? graph.nodes.filter(node => node.kind === 'modifier') : EMPTY_NODES,
    [graph.nodes, mixerOpen],
  );
  const timelineNodeIds = useMemo(
    () => new Set(graph.nodes.map(node => node.id)),
    [nodeStructureSignature],
  );
  const timelineAutomationTargets = useMemo<TimelineAutomationTarget[]>(() => {
    if (!timelineOpen) return [];
    const nodesById = new Map(graph.nodes.map(node => [node.id, node]));
    const downstream = new Map<string, string[]>();
    for (const link of graph.links) {
      if (link.kind !== 'source-modifier' && link.kind !== 'modifier-modifier') continue;
      const list = downstream.get(link.from);
      if (list) list.push(link.to);
      else downstream.set(link.from, [link.to]);
    }

    const reachableByTrack = new Map<string, Set<string>>();
    const reachCount = new Map<string, number>();
    for (const track of timeline.project.tracks) {
      const start = track.sourceNodeId ?? track.generatorNodeId;
      const reachable = new Set<string>();
      if (start) {
        const pending = [start];
        for (let index = 0; index < pending.length; index++) {
          const id = pending[index];
          if (reachable.has(id)) continue;
          reachable.add(id);
          for (const next of downstream.get(id) ?? []) {
            if (!reachable.has(next)) pending.push(next);
          }
        }
      }

      reachableByTrack.set(track.id, reachable);
      for (const id of reachable) {
        reachCount.set(id, (reachCount.get(id) ?? 0) + 1);
      }
    }

    const targets: TimelineAutomationTarget[] = [];
    for (const track of timeline.project.tracks) {
      for (const id of reachableByTrack.get(track.id) ?? []) {
        const node = nodesById.get(id);
        if (!node || !(node.kind === 'modifier' ||
          node.isMetronome || node.isToneGenerator || node.isSynthGenerator)) {
          continue;
        }

        const section = (reachCount.get(id) ?? 0) > 1 ? 'shared' : 'track';
        for (const parameter of node.parameters) {
          targets.push({
            nodeId: node.id,
            trackId: track.id,
            section,
            label: `${node.title} · ${parameter.name}`,
            processorIndex: parameter.processorIndex,
            parameterIndex: parameter.parameterIndex,
            min: parameter.min,
            max: parameter.max,
            value: parameter.value,
          });
        }
      }

      if (!track.generatorNodeId) {
        for (const clip of track.clips.filter(clip => Boolean(clip.sourcePath))) {
          targets.push({
            nodeId: `clip:${clip.id}`,
            trackId: track.id,
            section: 'track',
            label: `${track.name} · ${clip.name || t('Audio clip')} · ${t('Clip pitch')}`,
            processorIndex: -1,
            parameterIndex: 0,
            min: -24,
            max: 24,
            value: clip.pitchSemitones ?? 0,
          });
        }
      }
    }

    return targets;
  }, [linkStructureSignature, nodeStructureSignature, timeline.project.tracks, timelineOpen, t]);
  const menuNode = canvasMenu?.kind === 'node' ? nodes.find(node => node.id === canvasMenu.id) : undefined;
  const menuLink = canvasMenu?.kind === 'edge' ? graph.links.find(link => link.id === canvasMenu.id) : undefined;
  const menuDbVertical = menuNode?.data.isDbMeter
    ? (getNodeTelemetry(menuNode.id)?.dbMeterVertical ?? true)
    : true;
  const menuLeft = canvasMenu ? Math.min(canvasMenu.x, window.innerWidth - 230) : 0;
  const menuTop = canvasMenu ? Math.min(canvasMenu.y, window.innerHeight - 300) : 0;

  return (
    <I18nProvider language={language }>
    <div
      ref={appShellRef}
      className={`app-shell ${settings.signalGlow ? 'signal-glow' : ''} ${settings.connectorAnimation ? 'connector-animation' : ''} ${settings.highlightCableOnHover ? 'cable-hover' : ''} ${settings.cableAnimation ? 'cable-animation' : ''} ${altPressed ? 'alt' : ''}`}
      onPointerDownCapture={event => {
        const target = event.target as Element | null;
        if (target?.closest('.timeline-dock')) activeSurfaceRef.current = 'timeline';
        else if (target?.closest('.react-flow')) activeSurfaceRef.current = 'graph';
      }}
      onFocusCapture={event => {
        const target = event.target as Element | null;
        if (target?.closest('.timeline-dock')) activeSurfaceRef.current = 'timeline';
        else if (target?.closest('.react-flow')) activeSurfaceRef.current = 'graph';
      }}
    >
      <TitleBar version={settings.appVersion} />
      <Toolbar
        running={running}
        audioPlaying={audioPlaying}
        micMuted={ui.micMuted}
        canUndo={history.canUndo}
        canRedo={history.canRedo}
        undoLabel={history.undoLabel}
        redoLabel={history.redoLabel}
        onTransport={value => send('transport', { running: value })}
        onRefresh={() => send('refresh')}
        onSave={() => send('save-preset')}
        onLoad={() => send('load-preset')}
        onClear={requestClear}
        onUndo={() => send('undo')}
        onRedo={() => send('redo')}
        onTidy={tidyGraph}
        onMicMute={() => send('mic-mute', { value: !ui.micMuted })}
        onSettings={() => { setSettingsTab('behavior'); setShowSettings(true); }}
        mixerOpen={mixerOpen}
        onMixer={() => setMixerOpen(value => !value)}
        diagnosticsOpen={settings.showDiagnostics}
        onDiagnostics={toggleDiagnostics}
        timelineOpen={timelineOpen}
        onTimeline={toggleTimeline}
        onCheckRoute={() => setShowRouteCheck(true)}
      />
      {alert && <div className={`alert-bar ${alert.isError ? 'error' : ''}`} role="alert" aria-live="assertive"><span>{alert.text}</span><div className="alert-actions">{alert.action && alert.actionLabel && <button onClick={() => { send(alert.action!); setAlert(null); }}>{t(alert.actionLabel)}</button>}<button onClick={() => send('open-log') }>{t('Open log')}</button><button onClick={() => setAlert(null) }>{t('Dismiss')}</button></div></div>}
      {toast && <div className={`toast ${toast.isError ? 'error' : ''}`} role="status" aria-live="polite"><span>{toast.text}</span><button aria-label={t('Dismiss')} onClick={() => setToast(null)}>×</button></div>}
      {!catalog.perAppCaptureAvailable && (
        <div className="routing-warning-bar info" role="status">
          <Info size={15} />
          <span className="routing-warning-copy">{t('Per-app capture needs Windows 11 or Windows Server 2022. Use System audio or a virtual cable instead.')}</span>
        </div>
      )}
      {warningVisible && primaryWarning && (
        <div className={`routing-warning-bar ${primaryWarning.severity === 'info' ? 'info' : ''}`} role="alert">
          {primaryWarning.severity === 'info' ? <Info size={15} /> : <AlertTriangle size={15} />}
          <span className="routing-warning-copy">{primaryWarning.message}</span>
          {(graph.warnings?.length ?? 0) > 1 && <span className="routing-warning-count">+{(graph.warnings?.length ?? 1) - 1}</span>}
          <button className="routing-warning-action" onClick={() => setShowAudioHelp(true) }>{t('How to fix')}</button>
          <button className="routing-warning-dismiss" title={t('Dismiss warning')} aria-label={t('Dismiss warning')} onClick={() => setDismissedWarning(warningSignature)}><X size={14} /></button>
        </div>
      )}
      <div ref={workspaceRef} className={`workspace ${sidebarCollapsed ? 'sidebar-collapsed' : ''} ${mixerOpen ? 'mixer-open' : ''}`}>
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
              media={media}
              waveforms={mediaWaveforms}
              usedDeviceIds={usedDeviceIds}
              onAdd={quickAdd}
              onRename={setRenameLibrary}
              onDelete={setLibraryRemoval}
              onNewChain={newChain}
              onRescanPlugins={rescanPlugins}
              onEditChain={editChain}
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
          aria-label={t('Graph')}
          onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; }}
          onDrop={event => {
            event.preventDefault();
            const files = Array.from(event.dataTransfer.files ?? []);
            if (files.length > 0) {
              const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
              const output = nodesRef.current.find(node => {
                if (node.data.kind !== 'output') return false;
                const width = node.measured?.width ?? node.data.width ?? 204;
                const height = node.measured?.height ?? 140;
                return position.x >= node.position.x && position.x <= node.position.x + width &&
                  position.y >= node.position.y && position.y <= node.position.y + height;
              });
              const spot = output
                ? { x: Math.max(0, output.position.x - 240), y: output.position.y }
                : findFreeSpot({ x: position.x - 102, y: position.y - 60 });
              window.chrome?.webview?.postMessageWithAdditionalObjects?.(
                { type: 'file-drop', payload: { x: spot.x, y: spot.y, connectTo: output?.id } },
                files,
              );
              return;
            }

            const mediaListRaw = event.dataTransfer.getData('application/ruse-media-list');
            const mediaPath = event.dataTransfer.getData('application/ruse-media');
            let mediaPaths: string[] = [];
            if (mediaListRaw) {
              try {
                const parsed: unknown = JSON.parse(mediaListRaw);
                if (Array.isArray(parsed)) {
                  mediaPaths = parsed.filter((path): path is string => typeof path === 'string' && path.length > 0);
                }
              } catch {
                mediaPaths = [];
              }
            } else if (mediaPath) {
              mediaPaths = [mediaPath];
            }
            if (mediaPaths.length > 0) {
              const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
              const output = nodesRef.current.find(node => {
                if (node.data.kind !== 'output') return false;
                const width = node.measured?.width ?? node.data.width ?? 204;
                const height = node.measured?.height ?? 140;
                return position.x >= node.position.x && position.x <= node.position.x + width &&
                  position.y >= node.position.y && position.y <= node.position.y + height;
              });
              const spot = output
                ? { x: Math.max(0, output.position.x - 240), y: output.position.y }
                : findFreeSpot({ x: position.x - 102, y: position.y - 60 });
              mediaPaths.forEach((path, index) => {
                send('add-file-player', {
                  path,
                  x: output ? Math.max(0, spot.x - (index % 3) * 220) : spot.x + (index % 3) * 220,
                  y: spot.y + Math.floor(index / 3) * 170,
                  connectTo: output?.id,
                });
              });
              return;
            }

            const raw = event.dataTransfer.getData('application/ruse-item');
            if (!raw) return;
            const item = JSON.parse(raw) as CatalogItem;
            const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
            if (item.kind === 'template') {
              send('insert-template', {
                id: item.id,
                x: Math.round((position.x - 180) / 20) * 20,
                y: Math.round((position.y - 100) / 20) * 20,
              });
              return;
            }

            const canInsert = item.kind === 'effect' || item.kind === 'chain' || item.kind === 'vst3';
            const linkId = canInsert ? findLinkAtPoint(position) : undefined;
            const halfWidth = item.kind === 'vst3' ? 170 : 102;
            let addPosition = linkId
              ? { x: position.x - halfWidth, y: position.y - 60 }
              : findFreeSpot({ x: position.x, y: position.y }, halfWidth * 2);
            let connectTo = '';
            if (item.kind === 'sample' && item.filePath) {
              const output = nodesRef.current.find(node => {
                if (node.data.kind !== 'output') return false;
                const width = node.measured?.width ?? node.data.width ?? 204;
                const height = node.measured?.height ?? 140;
                return position.x >= node.position.x && position.x <= node.position.x + width &&
                  position.y >= node.position.y && position.y <= node.position.y + height;
              });
              if (output) {
                addPosition = { x: Math.max(0, output.position.x - 240), y: output.position.y };
                connectTo = output.id;
              }
            }
            sendAdd(item, addPosition, linkId, connectTo);
          }}
        >
          <ReactFlow
            nodes={nodes}
            edges={edges}
            tabIndex={0}
            aria-label={t('Routing canvas')}
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
              if (!(await confirmRecorderRemoval(deletable.map(node => node.id)))) return false;
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
            onMove={(_event, viewport) => setZoom(current =>
              Math.round(current * 100) === Math.round(viewport.zoom * 100) ? current : viewport.zoom)}
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
            onlyRenderVisibleElements
            proOptions={{ hideAttribution: true }}
            defaultEdgeOptions={{ type: 'ruseEdge' }}
          >
            <Background variant={BackgroundVariant.Dots} gap={24} size={1.15} color={
              theme === 'light' ? '#C5CFDD'
                : theme === 'blueprint' ? '#2B6BA8'
                : theme === 'studio74' ? '#4A5D4E'
                : theme === 'receiver' ? '#8E8271'
                : '#34465A'
            } />
            <ViewportPortal>
              <div className={`ruse-frame-layer ${altPressed ? 'alt' : ''}` }>
                {frameDraft && <div className="ruse-frame draft" style={{ left: frameDraft.x, top: frameDraft.y, width: frameDraft.width, height: frameDraft.height }} />}
                {frames.map(frame => (
                  <div className="ruse-frame" key={frame.id} style={{ left: frame.x, top: frame.y, width: frame.width, height: frame.height } }>
                    <div className="ruse-frame-header" onPointerDown={event => moveFrame(event, frame)}>
                      <span
                        className="ruse-frame-title"
                        title={t('Double-click to rename')}
                        onDoubleClick={event => { event.stopPropagation(); setFrameRename(frame); }}
                      >{frame.name || t('Group')}</span>
                      <button
                        className="ruse-frame-remove ruse-frame-save"
                        title={t('Save as template')}
                        aria-label={t('Save {name} as template', { name: frame.name })}
                        onPointerDown={event => event.stopPropagation()}
                        onClick={event => { event.stopPropagation(); send('save-frame-template', { id: frame.id }); }}
                      ><LayoutTemplate size={11} /></button>
                      <button
                        className="ruse-frame-remove"
                        title={t('Remove group')}
                        aria-label={t('Remove {name}', { name: frame.name })}
                        onPointerDown={event => event.stopPropagation()}
                        onClick={event => { event.stopPropagation(); requestRemoveFrame(frame); }}
                      ><X size={11} /></button>
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
            </ViewportPortal>            <Panel position="top-left" className="graph-caption"><span>{t('Routing canvas')}</span><strong>{t('{nodes} nodes · {cables} cables', { nodes: graphNodeCount, cables: edges.length })}</strong></Panel>
            {!frameTipDismissed && frames.length === 0 && graphNodeCount >= 2 && (
              <Panel position="bottom-center" className="frame-tip">
                <span>{t('Hold Ctrl and drag on empty canvas to draw a Frame. Drag its title bar to move the whole group.')}</span>
                <button type="button" className="icon-button compact" aria-label={t('Close')} title={t('Close')} onClick={() => setFrameTipDismissed(true)}><X size={11} /></button>
              </Panel>
            )}
            <Panel position="bottom-left" className="canvas-controls">
              <button title={t('Zoom out')} aria-label={t('Zoom out')} onClick={() => flow.zoomOut({ duration: 120 }) }><Minus size={14} /></button>
              <button title={t('Reset zoom')} aria-label={t('Reset zoom')} onClick={() => flow.zoomTo(1, { duration: 160 }) }>{Math.round(zoom * 100)}%</button>
              <button title={t('Zoom in')} aria-label={t('Zoom in')} onClick={() => flow.zoomIn({ duration: 120 }) }><Plus size={14} /></button>
              <button title={t('Fit graph (F)')} aria-label={t('Fit graph')} onClick={fitGraph }><Maximize2 size={14} /></button>
            </Panel>
            {showMiniMap && <MiniMap
              className="ruse-minimap"
              style={timelineOpen ? { bottom: 'min(var(--timeline-dock-height, 340px), calc(100% - 120px))' } : undefined}
              pannable
              zoomable
              nodeColor={node => (node.data as RuseFlowNode['data']).accent}
              nodeStrokeWidth={2}
              maskColor="rgba(7, 10, 14, 0.76)"
            />}
            <RouteHintOverlay nodes={nodes} links={graph.links} />
          </ReactFlow>
          {graphNodeCount === 0 && <div className="graph-empty">
            <span className="eyebrow">{t('Start with a signal')}</span>
            <h2>{t('Build your first route')}</h2>
            <p>{t('Add a source and an output, then drag from OUT to IN. Processing nodes sit between them.')}</p>
            <div className="empty-steps"><span><b>1</b> {t('Source')}</span><span><b>2</b> {t('Processing')}</span><span><b>3</b> {t('Destination')}</span></div>
            <div className="graph-empty-actions">
              <button className="primary-button" onClick={() => quickAdd(systemItem) }>{t('Add system audio')}</button>
              <button className="tool-button" onClick={openFirstRoute}>{t('Set up my first route')}</button>
            </div>
            <small>{t('Or drag anything from the library onto the canvas.')}</small>
          </div>}
        </main>
        {mixerOpen && <MixerPanel
          sources={mixerSources}
          outputs={mixerOutputs}
          modifiers={mixerModifiers}
          links={graph.links}
          onClose={closeMixer}
          onExplain={() => openExplainer('mixer')}
          onResizeStart={startMixerResize}
          onFocusNode={focusMixerNode}
        />}
        {timelineOpen && (
          <Suspense fallback={<div className="timeline-loading">{t('Loading timeline...')}</div>}>
            <TimelineBoundary onClose={closeTimeline} onError={closeTimeline}>
            <TimelinePanel
              state={timeline}
              automationTargets={timelineAutomationTargets}
              showIntro={timelineIntroVisible}
              onDismissIntro={dismissTimelineIntro}
              nodeIds={timelineNodeIds}
              onPlay={() => send('timeline-play')}
              onPause={() => send('timeline-pause')}
              onStop={() => send('timeline-stop')}
              onToggleRecord={() => send('timeline-record')}
              onAddTrack={() => send('timeline-add-track')}
              onImport={() => send('timeline-add-clip', { trackId: timeline.project.tracks[0]?.id ?? '' })}
              onSeek={sample => send('timeline-seek', { sample })}
              onAddClipPath={(trackId, path, startSample) => send('timeline-add-clip-path', { trackId, path, startSample })}
              onAddClipPaths={(trackId, paths, startSample) => send('timeline-add-clip-paths', { trackId, paths, startSample })}
              onAddGenerator={(trackId, nodeId, startSample) => send('timeline-add-generator', { trackId, nodeId, startSample })}
              onMoveClip={(trackId, clipId, startSample) => send('timeline-move-clip', { trackId, clipId, startSample })}
              onMoveClipToTrack={(fromTrackId, toTrackId, clipId, startSample) => send('timeline-move-clip-track', { fromTrackId, toTrackId, clipId, startSample })}
              onUpdateClip={(trackId, clipId, update) => send('timeline-update-clip', { trackId, clipId, ...update })}
              onStretchClip={(trackId, clipId, lengthSamples) => send('timeline-stretch-clip', { trackId, clipId, lengthSamples })}
              onSplitClip={(trackId, clipId, sample) => send('timeline-split-clip', { trackId, clipId, sample })}
              onDuplicateClip={(trackId, clipId) => send('timeline-duplicate-clip', { trackId, clipId })}
              onCrossfadeClip={(trackId, clipId) => send('timeline-crossfade-clip', { trackId, clipId })}
              onRemoveClip={(trackId, clipId) => send('timeline-remove-clip', { trackId, clipId })}
              onSetLoop={(startSample, endSample) => startSample == null ? send('timeline-loop', {}) : send('timeline-loop', { startSample, endSample })}
              onSetTempo={tempo => send('timeline-tempo', { tempo })}
              onSetTimeSignature={(numerator, denominator) => send('timeline-time-signature', { numerator, denominator })}
              onAddMarker={(startSample, endSample) => send('timeline-marker-add', { startSample, endSample })}
              onUpdateMarker={(id, name) => send('timeline-marker-update', { id, name })}
              onRemoveMarker={id => send('timeline-marker-remove', { id })}
              onCopyClips={clips => send('timeline-copy-clips', { clips })}
              onPasteClips={placements => send('timeline-paste-clipboard', { placements })}
              onNormalizeClip={(trackId, clipId) => send('timeline-normalize-clip', { trackId, clipId })}
              onReverseClip={(trackId, clipId) => send('timeline-reverse-clip', { trackId, clipId })}
              onConsolidateClips={clips => send('timeline-consolidate', { clips })}
              onConsolidateRange={(startSample, endSample) => send('timeline-consolidate', { startSample, endSample })}
              onBounce={() => send('timeline-bounce')}
              onBounceRange={() => send('timeline-bounce-range')}
              onCancelBounce={() => send('timeline-bounce-cancel')}
              onToggleMetronome={() => send('timeline-metronome', { value: !timeline.metronome })}
              onCountIn={() => send('timeline-count-in')}
              onSetCountIn={value => send('timeline-count-in-beats', { value })}
              onFreeze={() => send('timeline-freeze')}
              onTrackUpdate={(trackId, gain, muted, solo) => send('timeline-track-update', { trackId, gain, muted, solo })}
              onAddAutomation={(trackId, target) => send('timeline-automation-add', {
                trackId,
                nodeId: target.nodeId,
                processorIndex: target.processorIndex,
                parameterIndex: target.parameterIndex,
                value: target.value,
              })}
              onSetAutomationPoints={(trackId, laneId, points) => send('timeline-automation-points', { trackId, laneId, points })}
              onRemoveAutomation={(trackId, laneId) => send('timeline-automation-remove', { trackId, laneId })}
              onRemoveTrack={trackId => {
                const track = timeline.project.tracks.find(item => item.id === trackId);
                setTimelineTrackRemoval({ id: trackId, name: track?.name ?? t('Track') });
              }}
              onRenameTrack={(trackId, name) => setTimelineTrackRename({ id: trackId, name })}
              onShowTrackNode={(trackId, sourceNodeId) => {
                if (sourceNodeId && graph.nodes.some(node => node.id === sourceNodeId)) {
                  focusMixerNode(sourceNodeId);
                } else {
                  send('timeline-show-node', { trackId });
                }
              }}
              onUndo={() => send('timeline-undo')}
              onRedo={() => send('timeline-redo')}
              onClose={closeTimeline}
            />
            </TimelineBoundary>
          </Suspense>
        )}
      </div>
      <StatusBar status={status} showDiagnostics={settings.showDiagnostics} />
      {showSettings && <SettingsDialog settings={settings} initialTab={settingsTab} outputs={[...catalog.outputs, ...catalog.virtuals]} virtualInputs={catalog.inputs.filter(item => item.virtual)} virtualOutputs={catalog.virtuals} vstPlugins={catalog.vst3Plugins} onFirstRoute={openFirstRoute} onChange={next => { setSettings(next); send('settings', { ...next }); }} onClose={() => setShowSettings(false)} />}
      {trayPrompt && <TrayPromptDialog
        onKeep={() => { setTrayPrompt(false); send('close-to-tray-result', { keep: true }); }}
        onQuit={() => { setTrayPrompt(false); send('close-to-tray-result', { keep: false }); }}
        onCancel={() => { setTrayPrompt(false); send('close-to-tray-result', { cancel: true }); }}
      />}
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
        onRemoveWithNodes={async () => {
          const removal = frameRemoval;
          const ids = removal.nodeIds;
          if (!(await confirmRecorderRemoval(ids))) return;
          removeFrameOnly(removal.frame);
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
      {libraryRemoval && <ConfirmDialog
        title={t('Delete {name}', { name: libraryRemoval.name })}
        body={t('This removes it from the library. Nodes already on the canvas are not affected.')}
        confirmLabel={t('Delete')}
        onConfirm={() => {
          send(libraryRemoval.kind === 'chain' ? 'chain-delete' : 'template-delete', { name: libraryRemoval.name });
          setLibraryRemoval(null);
        }}
        onClose={() => setLibraryRemoval(null)}
      />}
      {timelineTrackRemoval && <ConfirmDialog
        title={t('Remove timeline track')}
        body={t('Remove track “{name}” and all of its clips? Its track node will also be removed.', { name: timelineTrackRemoval.name })}
        confirmLabel={t('Remove')}
        onConfirm={() => {
          send('timeline-remove-track', { trackId: timelineTrackRemoval.id });
          setTimelineTrackRemoval(null);
        }}
        onClose={() => setTimelineTrackRemoval(null)}
      />}
      {timelineTrackRename && <PromptDialog
        title={t('Rename track')}
        label={t('Track name')}
        value={timelineTrackRename.name}
        submitLabel={t('Rename')}
        onSubmit={value => {
          send('timeline-track-rename', { trackId: timelineTrackRename.id, name: value });
          setTimelineTrackRename(null);
        }}
        onClose={() => setTimelineTrackRename(null)}
      />}
      {chainEditor && <ChainEditorDialog
        request={chainEditor}
        effects={catalog.effects.filter(item => item.id !== 'transcriber' && item.id !== 'translator')}
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
        <div className="context-menu" role="menu" aria-label={t('Canvas')} onKeyDown={onContextMenuKeyDown} style={{ left: menuLeft, top: menuTop } }>
          {canvasMenu.kind === 'node' && menuNode && <>
            <button role="menuitem" tabIndex={-1} onClick={() => { setRenamePrompt({ id: menuNode.id, title: menuNode.data.title }); setCanvasMenu(null); } }><Pencil size={14} /> <span className="menu-label">{t('Rename')}</span></button>
            <button role="menuitem" tabIndex={-1} onClick={() => { send('duplicate', { id: menuNode.id }); setCanvasMenu(null); } }><Copy size={14} /> <span className="menu-label">{t('Duplicate')}</span> <kbd>{SHORTCUTS.duplicate}</kbd></button>
            {menuNode.data.canFreezeEffects && <button role="menuitem" tabIndex={-1} onClick={() => { send('freeze-effects', { id: menuNode.id }); setCanvasMenu(null); } }><Snowflake size={14} /> <span className="menu-label">{t('Freeze effects')}</span></button>}
            <button role="menuitem" tabIndex={-1} onClick={() => { send('mute', { id: menuNode.id, value: !menuNode.data.muted }); setCanvasMenu(null); } }>{menuNode.data.muted ? <Volume2 size={14} /> : <VolumeX size={14} />} <span className="menu-label">{menuNode.data.muted ? t('Unmute node') : t('Mute node')}</span> <kbd>{SHORTCUTS.muteSelected}</kbd></button>
            {menuNode.data.isDbMeter && <button role="menuitem" tabIndex={-1} onClick={() => { send('db-meter-orientation', { id: menuNode.id, vertical: !menuDbVertical }); setCanvasMenu(null); } }><ArrowUpDown size={14} /> <span className="menu-label">{menuDbVertical ? t('Make horizontal') : t('Make vertical')}</span></button>}
            <button role="menuitem" tabIndex={-1} onClick={() => { send('disconnect-node', { id: menuNode.id }); setCanvasMenu(null); } }><Unplug size={14} /> <span className="menu-label">{t('Disconnect routes')}</span></button>
            <div className="menu-separator" role="separator" />
            <button role="menuitem" tabIndex={-1} className="danger" onClick={() => { send('remove', { id: menuNode.id }); setCanvasMenu(null); } }><Trash2 size={14} /> <span className="menu-label">{t('Remove node')}</span> <kbd>{SHORTCUTS.remove}</kbd></button>
          </>}
          {canvasMenu.kind === 'edge' && menuLink && <>
            {menuLink.kind === 'direct' && <>
              <button role="menuitem" tabIndex={-1} aria-pressed={menuLink.native === true} onClick={() => { send('link-native', { id: menuLink.id, value: menuLink.native !== true }); setCanvasMenu(null); } }><Zap size={14} /> <span className="menu-label">{t('Native passthrough')}{menuLink.native === true ? ' ✓' : ''}</span></button>
              {menuLink.nativeFallback && <button role="menuitem" tabIndex={-1} onClick={() => { send('link-native-cancel-restore', { id: menuLink.id }); setCanvasMenu(null); } }><Zap size={14} /> <span className="menu-label">{t('Cancel native restore')}</span></button>}
              <button role="menuitem" tabIndex={-1} onClick={() => { openExplainer('native'); setCanvasMenu(null); } }><Info size={14} /> <span className="menu-label">{t('About native passthrough')}</span></button>
            </>}
            <button role="menuitem" tabIndex={-1} className="danger" onClick={() => { send('disconnect', { id: menuLink.id }); setCanvasMenu(null); } }><Unplug size={14} /> <span className="menu-label">{t('Disconnect cable')}</span></button>
          </>}
          {canvasMenu.kind === 'pane' && <>
            <button role="menuitem" tabIndex={-1} onClick={() => {
              if (canvasMenu.flowX != null && canvasMenu.flowY != null) sendAdd(systemItem, findFreeSpot({ x: canvasMenu.flowX, y: canvasMenu.flowY }));
              setCanvasMenu(null);
            } }><Plus size={14} /> <span className="menu-label">{t('Add system audio')}</span></button>
            <button role="menuitem" tabIndex={-1} onClick={() => { fitGraph(); setCanvasMenu(null); } }><Maximize2 size={14} /> <span className="menu-label">{t('Fit graph')}</span> <kbd>{SHORTCUTS.fit}</kbd></button>
            <button role="menuitem" tabIndex={-1} onClick={tidyGraph }><LayoutGrid size={14} /> <span className="menu-label">{t('Tidy up')}</span> <kbd>{SHORTCUTS.tidy}</kbd></button>
            <div className="menu-separator" role="separator" />
            <button role="menuitem" tabIndex={-1} className="danger" onClick={() => { setCanvasMenu(null); void requestClear(); } }><Trash2 size={14} /> <span className="menu-label">{t('Clear graph')}</span></button>
          </>}
        </div>
      </div>}
      {explainer && <Suspense fallback={null}><ExplainerDialog id={explainer} onClose={() => setExplainer(null)} /></Suspense>}
      {firstRouteOpen && <Suspense fallback={null}><FirstRouteDialog catalog={catalog} result={firstRouteResult} onClose={() => setFirstRouteOpen(false)} /></Suspense>}
      {processPicker && <Suspense fallback={null}><ProcessPickerDialog nodeId={processPicker.id} nodeTitle={processPicker.title} processId={processPicker.processId} processName={processPicker.processName} apps={catalog.apps} onClose={() => setProcessPicker(null)} /></Suspense>}
      {showRouteCheck && <Suspense fallback={null}><RouteCheckDialog nodes={nodes} links={graph.links} warnings={graph.warnings} running={running} onOpenFirstRoute={() => { setShowRouteCheck(false); openFirstRoute(); }} onOpenMixer={() => { setShowRouteCheck(false); setMixerOpen(true); }} onClose={() => setShowRouteCheck(false)} /></Suspense>}
      {welcomeOpen && <Suspense fallback={null}><WelcomeWizard
        settings={settings}
        onChange={next => { setSettings(next); send('settings', { ...next }); }}
        onClose={() => { setWelcomeOpen(false); send('onboarding-complete'); }}
        onCreateDesktopShortcut={() => send('create-desktop-shortcut')}
      /></Suspense>}
      {showAudioHelp && <DuplicateAudioDialog warning={primaryWarning} onClose={() => setShowAudioHelp(false)} />}
    </div>
    {startup.visible && (
      <div className="startup-overlay" role="status" aria-live="polite">
        <div className="startup-card">
          <div className="startup-logo"><AudioLines size={26} /></div>
          <strong>{t('Starting Ruse')}</strong>
          <div className="startup-progress"><span style={{ width: `${Math.round(startup.progress * 100)}%` }} /></div>
          <small>{t('Preparing audio engine...')}</small>
        </div>
      </div>
    )}
    </I18nProvider>
  );
}
