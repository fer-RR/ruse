import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  Background, BackgroundVariant, MiniMap, Panel, ReactFlow, ViewportPortal,
  useNodesState, useReactFlow, useStore,
} from '@xyflow/react';
import type { Connection, Edge, EdgeChange, NodeChange } from '@xyflow/react';
import {
  AlertTriangle, ArrowUpDown, AudioLines, ChevronLeft, ChevronRight, Copy, Info, LayoutGrid, Maximize2,
  LayoutTemplate, Minus, Pencil, Plus, Trash2, Unplug, Volume2, VolumeX, X, Zap,
} from 'lucide-react';
import { isHosted, send, subscribe } from './bridge';
import { createDemoTelemetry, demoCatalog, demoGraph, demoSettings } from './demo';
import { I18nProvider, normalizeLanguage, translate } from './i18n';
import type { RuseFlowNode } from './flow';
import { nearestFreePosition } from './layout';
import { catalogKey } from './library';
import { SHORTCUTS } from './shortcuts';
import { getNodeTelemetry, publishTelemetry, setNodeTitles, useUiTelemetry } from './telemetry';
import RuseNode from './components/RuseNode';
import RuseEdge from './components/RuseEdge';
import RuseConnectionLine from './components/RuseConnectionLine';
import Sidebar from './components/Sidebar';
import { StatusBar, TitleBar, Toolbar } from './components/Chrome';
import { ConfirmDialog, DuplicateAudioDialog, PromptDialog, RemoveFrameDialog, SettingsDialog, TrayPromptDialog } from './components/Dialogs';
import type { SettingsTab } from './components/Dialogs';
import ChainEditorDialog from './components/ChainEditorDialog';
import WelcomeWizard from './components/WelcomeWizard';
import type { ExplainerId } from './components/Explainers';
import MixerPanel from './components/MixerPanel';
import { DIAGNOSTICS_NODE_ID, loadDiagnosticsLayout, saveDiagnosticsLayout } from './diagnostics';
import type {
  CatalogItem, CatalogSnapshot, ChainEditorRequest, FrameModel, GraphSnapshot, HistorySnapshot,
  LinkStateUpdate, NodeStateUpdate, SettingsModel, StatusMessage, TelemetrySnapshot,
} from './types';
import type { TimelineAutomationTarget, TimelineState } from './timeline';

const TimelinePanel = lazy(() => import('./components/TimelinePanel'));
const ExplainerDialog = lazy(() => import('./components/Explainers'));
const nodeTypes = { ruseNode: RuseNode };
const edgeTypes = { ruseEdge: RuseEdge };
const FRAME_HANDLES = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'] as const;
type FrameHandle = typeof FRAME_HANDLES[number];
const emptyCatalog: CatalogSnapshot = {
  apps: [], outputs: [], inputs: [], virtuals: [], effects: [], chains: [], vst3Plugins: [], frameTemplates: [], samples: [],
  defaultOutputId: '', defaultOutputName: '', perAppCaptureAvailable: true, asioLoaded: false,
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
  const [systemPrefersDark, setSystemPrefersDark] = useState(() =>
    typeof window === 'undefined' || !window.matchMedia
      ? true
      : window.matchMedia('(prefers-color-scheme: dark)').matches);
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const query = window.matchMedia('(prefers-color-scheme: dark)');
    const update = () => setSystemPrefersDark(query.matches);
    update();
    query.addEventListener?.('change', update);
    return () => query.removeEventListener?.('change', update);
  }, []);
  const theme = settings.theme === 'system'
    ? (systemPrefersDark ? 'dark' : 'light')
    : settings.theme;
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
  const [timeline, setTimeline] = useState<TimelineState>({
    project: { version: 1, sampleRate: 48000, tempo: 120, loopStartSample: -1, loopEndSample: -1, tracks: [] },
    playheadSample: 0,
    playing: false,
    sampleRate: 48000,
    canUndo: false,
    canRedo: false,
    recording: false,
  });
  const [mixerWidth, setMixerWidth] = useState(() => {
    const saved = Number(localStorage.getItem('ruse.mixerWidth'));
    return saved >= 240 && saved <= 520 ? saved : 320;
  });
  const [showSettings, setShowSettings] = useState(false);
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('behavior');
  const [showAudioHelp, setShowAudioHelp] = useState(false);
  const [welcomeOpen, setWelcomeOpen] = useState(false);
  const [showClear, setShowClear] = useState(false);
  const [timelineTrackRemoval, setTimelineTrackRemoval] = useState<{ id: string; name: string } | null>(null);
  const [trayPrompt, setTrayPrompt] = useState(false);
  const [renamePrompt, setRenamePrompt] = useState<RenamePrompt | null>(null);
  const [canvasMenu, setCanvasMenu] = useState<CanvasMenu | null>(null);
  const [chainEditor, setChainEditor] = useState<ChainEditorRequest | null>(null);
  const [renameLibrary, setRenameLibrary] = useState<CatalogItem | null>(null);
  const [libraryRemoval, setLibraryRemoval] = useState<CatalogItem | null>(null);
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
  const [rejectedLinkId, setRejectedLinkId] = useState<string | null>(null);

  const nodesRef = useRef(nodes);
  const settingsRef = useRef(settings);
  const sidebarCollapsedRef = useRef(sidebarCollapsed);
  const zCounter = useRef(1);
  useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  useEffect(() => { framesRef.current = frames; }, [frames]);
  useEffect(() => { settingsRef.current = settings; }, [settings]);
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
  const openExplainer = useCallback((id: ExplainerId) => {
    localStorage.setItem(`ruse.explainer.${id}`, '1');
    setExplainer(id);
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
  useEffect(() => { localStorage.setItem('ruse.mixerOpen', mixerOpen ? '1' : '0'); }, [mixerOpen]);
  useEffect(() => {
    localStorage.setItem('ruse.mixerWidth', String(mixerWidth));
    workspaceRef.current?.style.setProperty('--mixer-width', `${mixerWidth}px`);
  }, [mixerWidth]);
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
      case 'node-state': {
        const update = payload as NodeStateUpdate;
        setGraph(current => ({
          ...current,
          nodes: current.nodes.map(node => node.id === update.id
            ? {
                ...node,
                volume: update.volume ?? node.volume,
                muted: update.muted ?? node.muted,
                solo: update.solo ?? node.solo,
                enabled: update.enabled ?? node.enabled,
                send: update.send ?? node.send,
                isParallel: update.parallel ?? node.isParallel,
                color: update.color !== undefined ? (update.color || undefined) : node.color,
                accent: update.accent ?? node.accent,
              }
            : node),
        }));
        setNodes(current => current.map(node => node.id === update.id
          ? {
              ...node,
              data: {
                ...node.data,
                volume: update.volume ?? node.data.volume,
                muted: update.muted ?? node.data.muted,
                solo: update.solo ?? node.data.solo,
                enabled: update.enabled ?? node.data.enabled,
                send: update.send ?? node.data.send,
                isParallel: update.parallel ?? node.data.isParallel,
                color: update.color !== undefined ? (update.color || undefined) : node.data.color,
                accent: update.accent ?? node.data.accent,
              },
            }
          : node));
        break;
      }
      case 'link-state': {
        const update = payload as LinkStateUpdate;
        setGraph(current => ({
          ...current,
          links: current.links.map(link => link.id === update.id
            ? { ...link, gain: update.gain, enabled: update.enabled, native: update.native, nativeRate: update.nativeRate }
            : link),
        }));
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
        setNodes(current => current.map(node => {
          if (node.id !== update.id) return node;
          const parameters = node.data.parameters.map(parameter =>
            parameter.processorIndex === update.processor && parameter.parameterIndex === update.parameter
              ? { ...parameter, value: update.value }
              : parameter);
          return { ...node, data: { ...node.data, parameters } };
        }));
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
        targetHandle: link.kind === 'sidechain' ? 'sidechain' : 'in',
        type: 'ruseEdge',
        data: { kind: link.kind, native: link.native === true, nativeRate: link.native === true ? (link.nativeRate || source?.sampleRate) : undefined, rejected: rejectedLinkId === link.id },
        animated: false,
        interactionWidth: 22,
        style: { stroke: source?.accent ?? '#5B8DEF', strokeWidth: 2, opacity: 0.84 },
      };
    });
  }, [graph.links, graph.nodes, rejectedLinkId]);

  const sendAdd = useCallback((item: CatalogItem, position: { x: number; y: number }, linkId?: string) => {
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
      duration: 220,
    });
  }, [flow, setNodes]);

  const findFreeSpot = useCallback((position: { x: number; y: number }) => {
    const others = nodesRef.current.map(node => ({
      id: node.id,
      x: node.position.x,
      y: node.position.y,
      width: node.measured?.width ?? 204,
      height: node.measured?.height ?? 140,
    }));
    return nearestFreePosition({ id: 'new', x: position.x, y: position.y, width: 204, height: 140 }, others);
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

    sendAdd(item, findFreeSpot({ x: position.x - 102, y: position.y - 60 }));
  }, [flow, sendAdd, findFreeSpot]);

  const newChain = useCallback(
    () => setChainEditor({ name: 'My chain', elements: [] }), []);
  const rescanPlugins = useCallback(() => send('vst3-rescan'), []);
  const refreshAsio = useCallback(() => send('asio-refresh'), []);
  const editChain = useCallback((item: CatalogItem) => setChainEditor({
    name: item.name,
    previousName: item.name,
    elements: (item.elements ?? []).map(element => ({ typeId: element.typeId, parameters: { ...element.parameters } })),
  }), []);

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
      send('connect', {
        from: connection.source,
        to: connection.target,
        kind: connection.targetHandle === 'sidechain' ? 'sidechain' : '',
      });
    }
  };

  const nodeById = useMemo(() => new Map(graph.nodes.map(node => [node.id, node])), [graph.nodes]);
  const isValidConnection = useCallback((connection: Connection | Edge) => {
    const source = connection.source ? nodeById.get(connection.source) : undefined;
    const target = connection.target ? nodeById.get(connection.target) : undefined;
    if (!source || !target || source.id === target.id) return false;
    if (source.isNoteDetector && connection.targetHandle !== 'sidechain') return false;
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
  const modalOpen = showSettings || showAudioHelp || welcomeOpen || explainer != null ||
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
      const target = event.target as HTMLElement;
      const textInput = target instanceof HTMLInputElement &&
        !['range', 'checkbox', 'radio', 'button', 'submit'].includes(target.type);
      const editing = textInput || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable;
      if (modalOpen) return;
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
      if (editing) return;
      const modifier = event.ctrlKey || event.metaKey;
      if (modifier && event.key.toLowerCase() === 'z') {
        event.preventDefault();
        send(event.shiftKey ? 'redo' : 'undo');
      } else if (modifier && event.key.toLowerCase() === 'y') {
        event.preventDefault();
        send('redo');
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
        setTimelineOpen(true);
        send('timeline-open');
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
      } else if (event.key === ' ' &&
                 !(target instanceof HTMLButtonElement) &&
                 !(target instanceof HTMLInputElement) &&
                 !(target instanceof HTMLSelectElement) &&
                 !(target instanceof HTMLTextAreaElement)) {
        event.preventDefault();
        if (!timelineOpen) {
          setTimelineOpen(true);
          send('timeline-open');
        }
        send(timeline.playing ? 'timeline-stop' : 'timeline-play');
      } else if (event.key.toLowerCase() === 'b') {
        const selected = nodesRef.current.find(node =>
          node.selected && node.data.kind === 'modifier' && !node.data.diagnostics);
        if (selected) {
          send('effect-enabled', { id: selected.id, value: selected.data.enabled === false });
        }
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
  }, [flow, toggleDiagnostics, modalOpen, running, ui.micMuted, timeline.playing, timelineOpen]);

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
  const mixerSources = useMemo(() => graph.nodes.filter(node => node.kind === 'source'), [graph.nodes]);
  const mixerOutputs = useMemo(() => graph.nodes.filter(node => node.kind === 'output'), [graph.nodes]);
  const mixerModifiers = useMemo(() => graph.nodes.filter(node => node.kind === 'modifier'), [graph.nodes]);
  const timelineNodeIds = useMemo(() => new Set(graph.nodes.map(node => node.id)), [graph.nodes]);
  const timelineAutomationTargets = useMemo<TimelineAutomationTarget[]>(() =>
    graph.nodes.filter(node => node.kind === 'modifier').flatMap(node => node.parameters.map(parameter => ({
      nodeId: node.id,
      label: `${node.title} · ${parameter.name}`,
      processorIndex: parameter.processorIndex,
      parameterIndex: parameter.parameterIndex,
      min: parameter.min,
      max: parameter.max,
      value: parameter.value,
    }))), [graph.nodes]);
  const menuNode = canvasMenu?.kind === 'node' ? nodes.find(node => node.id === canvasMenu.id) : undefined;
  const menuLink = canvasMenu?.kind === 'edge' ? graph.links.find(link => link.id === canvasMenu.id) : undefined;
  const menuDbVertical = menuNode?.data.isDbMeter
    ? (getNodeTelemetry(menuNode.id)?.dbMeterVertical ?? true)
    : true;
  const menuLeft = canvasMenu ? Math.min(canvasMenu.x, window.innerWidth - 230) : 0;
  const menuTop = canvasMenu ? Math.min(canvasMenu.y, window.innerHeight - 300) : 0;

  return (
    <I18nProvider language={language }>
    <div ref={appShellRef} className={`app-shell ${settings.signalGlow ? 'signal-glow' : ''} ${settings.connectorAnimation ? 'connector-animation' : ''} ${settings.highlightCableOnHover ? 'cable-hover' : ''} ${settings.cableAnimation ? 'cable-animation' : ''} ${altPressed ? 'alt' : ''}`}>
      <TitleBar />
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
        onClear={() => setShowClear(true)}
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
        onTimeline={() => { setTimelineOpen(true); send('timeline-open'); }}
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
              usedDeviceIds={usedDeviceIds}
              onAdd={quickAdd}
              onRename={setRenameLibrary}
              onDelete={setLibraryRemoval}
              onNewChain={newChain}
              onRescanPlugins={rescanPlugins}
              onEditChain={editChain}
              onAsioRefresh={refreshAsio}
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
              const spot = findFreeSpot({ x: position.x - 102, y: position.y - 60 });
              window.chrome?.webview?.postMessageWithAdditionalObjects?.(
                { type: 'file-drop', payload: { x: spot.x, y: spot.y } },
                files,
              );
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
            sendAdd(
              item,
              linkId ? { x: position.x - halfWidth, y: position.y - 60 } : findFreeSpot({ x: position.x, y: position.y }),
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
                : theme === 'midnight' ? '#22304A'
                : theme === 'ocean' ? '#1B4A52'
                : theme === 'ember' ? '#4A352C'
                : theme === 'blueprint' ? '#5A8FC7'
                : theme === 'deepdive' ? '#4C2C7A'
                : theme === 'studio74' ? '#4A5D4E'
                : theme === 'midnightstudy' ? '#2E3450'
                : '#34465A'
            } />
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
          {graphNodeCount === 0 && <div className="graph-empty">
            <span className="eyebrow">{t('Start with a signal')}</span>
            <h2>{t('Build your first route')}</h2>
            <p>{t('Add a source and an output, then drag from OUT to IN. Processing nodes sit between them.')}</p>
            <div className="empty-steps"><span><b>1</b> {t('Source')}</span><span><b>2</b> {t('Processing')}</span><span><b>3</b> {t('Destination')}</span></div>
            <button className="primary-button" onClick={() => quickAdd(systemItem) }>{t('Add system audio')}</button>
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
            <TimelinePanel
              state={timeline}
              automationTargets={timelineAutomationTargets}
              nodeIds={timelineNodeIds}
              onPlay={() => send('timeline-play')}
              onStop={() => send('timeline-stop')}
              onToggleRecord={() => send('timeline-record')}
              onAddTrack={() => send('timeline-add-track')}
              onImport={() => send('timeline-add-clip', { trackId: timeline.project.tracks[0]?.id ?? '' })}
              onSeek={sample => send('timeline-seek', { sample })}
              onAddClipPath={(trackId, path, startSample) => send('timeline-add-clip-path', { trackId, path, startSample })}
              onMoveClip={(trackId, clipId, startSample) => send('timeline-move-clip', { trackId, clipId, startSample })}
              onMoveClipToTrack={(fromTrackId, toTrackId, clipId, startSample) => send('timeline-move-clip-track', { fromTrackId, toTrackId, clipId, startSample })}
              onUpdateClip={(trackId, clipId, update) => send('timeline-update-clip', { trackId, clipId, ...update })}
              onStretchClip={(trackId, clipId, lengthSamples) => send('timeline-stretch-clip', { trackId, clipId, lengthSamples })}
              onRemoveClip={(trackId, clipId) => send('timeline-remove-clip', { trackId, clipId })}
              onSetLoop={(startSample, endSample) => startSample == null ? send('timeline-loop', {}) : send('timeline-loop', { startSample, endSample })}
              onSetTempo={tempo => send('timeline-tempo', { tempo })}
              onBounce={() => send('timeline-bounce')}
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
              onShowTrackNode={(trackId, sourceNodeId) => {
                if (sourceNodeId && graph.nodes.some(node => node.id === sourceNodeId)) {
                  focusMixerNode(sourceNodeId);
                } else {
                  send('timeline-show-node', { trackId });
                }
              }}
              onUndo={() => send('timeline-undo')}
              onRedo={() => send('timeline-redo')}
              onClose={() => setTimelineOpen(false)}
            />
          </Suspense>
        )}
      </div>
      <StatusBar status={status} />
      {showSettings && <SettingsDialog settings={settings} initialTab={settingsTab} outputs={[...catalog.outputs, ...catalog.virtuals]} virtualInputs={catalog.inputs.filter(item => item.virtual)} virtualOutputs={catalog.virtuals} vstPlugins={catalog.vst3Plugins} onChange={next => { setSettings(next); send('settings', { ...next }); }} onClose={() => setShowSettings(false)} />}
      {trayPrompt && <TrayPromptDialog
        onKeep={() => { setTrayPrompt(false); send('close-to-tray-result', { keep: true }); }}
        onQuit={() => { setTrayPrompt(false); send('close-to-tray-result', { keep: false }); }}
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
            <button role="menuitem" tabIndex={-1} onClick={() => { setRenamePrompt({ id: menuNode.id, title: menuNode.data.title }); setCanvasMenu(null); } }><Pencil size={14} /> {t('Rename')}</button>
            <button role="menuitem" tabIndex={-1} onClick={() => { send('duplicate', { id: menuNode.id }); setCanvasMenu(null); } }><Copy size={14} /> {t('Duplicate')} <kbd>{SHORTCUTS.duplicate}</kbd></button>
            <button role="menuitem" tabIndex={-1} onClick={() => { send('mute', { id: menuNode.id, value: !menuNode.data.muted }); setCanvasMenu(null); } }>{menuNode.data.muted ? <Volume2 size={14} /> : <VolumeX size={14} />} {menuNode.data.muted ? t('Unmute node') : t('Mute node')} <kbd>{SHORTCUTS.muteSelected}</kbd></button>
            {menuNode.data.isDbMeter && <button role="menuitem" tabIndex={-1} onClick={() => { send('db-meter-orientation', { id: menuNode.id, vertical: !menuDbVertical }); setCanvasMenu(null); } }><ArrowUpDown size={14} /> {menuDbVertical ? t('Make horizontal') : t('Make vertical')}</button>}
            <button role="menuitem" tabIndex={-1} onClick={() => { send('disconnect-node', { id: menuNode.id }); setCanvasMenu(null); } }><Unplug size={14} /> {t('Disconnect routes')}</button>
            <div className="menu-separator" role="separator" />
            <button role="menuitem" tabIndex={-1} className="danger" onClick={() => { send('remove', { id: menuNode.id }); setCanvasMenu(null); } }><Trash2 size={14} /> {t('Remove node')} <kbd>{SHORTCUTS.remove}</kbd></button>
          </>}
          {canvasMenu.kind === 'edge' && menuLink && <>
            {menuLink.kind === 'direct' && <>
              <button role="menuitem" tabIndex={-1} aria-pressed={menuLink.native === true} onClick={() => { send('link-native', { id: menuLink.id, value: menuLink.native !== true }); setCanvasMenu(null); } }><Zap size={14} /> {t('Native passthrough')}{menuLink.native === true ? ' ✓' : ''}</button>
              <button role="menuitem" tabIndex={-1} onClick={() => { openExplainer('native'); setCanvasMenu(null); } }><Info size={14} /> {t('About native passthrough')}</button>
            </>}
            <button role="menuitem" tabIndex={-1} className="danger" onClick={() => { send('disconnect', { id: menuLink.id }); setCanvasMenu(null); } }><Unplug size={14} /> {t('Disconnect cable')}</button>
          </>}
          {canvasMenu.kind === 'pane' && <>
            <button role="menuitem" tabIndex={-1} onClick={() => {
              if (canvasMenu.flowX != null && canvasMenu.flowY != null) sendAdd(systemItem, findFreeSpot({ x: canvasMenu.flowX, y: canvasMenu.flowY }));
              setCanvasMenu(null);
            } }><Plus size={14} /> {t('Add system audio')}</button>
            <button role="menuitem" tabIndex={-1} onClick={() => { fitGraph(); setCanvasMenu(null); } }><Maximize2 size={14} /> {t('Fit graph')} <kbd>{SHORTCUTS.fit}</kbd></button>
            <button role="menuitem" tabIndex={-1} onClick={tidyGraph }><LayoutGrid size={14} /> {t('Tidy up')} <kbd>{SHORTCUTS.tidy}</kbd></button>
            <div className="menu-separator" role="separator" />
            <button role="menuitem" tabIndex={-1} className="danger" onClick={() => { setShowClear(true); setCanvasMenu(null); } }><Trash2 size={14} /> {t('Clear graph')}</button>
          </>}
        </div>
      </div>}
      {explainer && <Suspense fallback={null}><ExplainerDialog id={explainer} onClose={() => setExplainer(null)} /></Suspense>}
      {welcomeOpen && <WelcomeWizard
        settings={settings}
        onChange={next => { setSettings(next); send('settings', { ...next }); }}
        onClose={() => { setWelcomeOpen(false); send('onboarding-complete'); }}
      />}
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
