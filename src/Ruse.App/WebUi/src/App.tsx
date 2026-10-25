import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Background, BackgroundVariant, MiniMap, Panel, ReactFlow,
  useNodesState, useReactFlow,
} from '@xyflow/react';
import type { Connection, Edge, EdgeChange, NodeChange } from '@xyflow/react';
import {
  Copy, LayoutGrid, Maximize2, Minus, Pencil, Plus, Trash2, Unplug,
  Volume2, VolumeX,
} from 'lucide-react';
import { isHosted, send, subscribe } from './bridge';
import { createDemoTelemetry, demoCatalog, demoGraph, demoSettings } from './demo';
import type { RuseFlowNode } from './flow';
import { nearestFreePosition } from './layout';
import { catalogKey } from './library';
import { publishTelemetry, setNodeTitles, useUiTelemetry } from './telemetry';
import RuseNode from './components/RuseNode';
import RuseEdge from './components/RuseEdge';
import Sidebar from './components/Sidebar';
import { StatusBar, TitleBar, Toolbar } from './components/Chrome';
import { ConfirmDialog, HelpDialog, PromptDialog, SettingsDialog } from './components/Dialogs';
import ChainEditorDialog from './components/ChainEditorDialog';
import type {
  CatalogItem, CatalogSnapshot, ChainEditorRequest, GraphSnapshot, HistorySnapshot,
  SettingsModel, StatusMessage, TelemetrySnapshot,
} from './types';

const nodeTypes = { ruseNode: RuseNode };
const edgeTypes = { ruseEdge: RuseEdge };
const emptyCatalog: CatalogSnapshot = { apps: [], outputs: [], inputs: [], virtuals: [], effects: [], chains: [] };
const initialSettings: SettingsModel = demoSettings;
const systemItem: CatalogItem = {
  id: 'system', kind: 'system', name: 'System audio', subtitle: 'Windows loopback',
  details: 'Capture everything playing through the default output',
};

interface RenamePrompt { id: string; title: string }
interface RoutePrompt { id: string; gain: number }
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
  const searchRef = useRef<HTMLInputElement>(null);
  const [catalog, setCatalog] = useState<CatalogSnapshot>(isHosted ? emptyCatalog : demoCatalog);
  const [graph, setGraph] = useState<GraphSnapshot>(isHosted ? { version: 0, nodes: [], links: [], running: false } : demoGraph);
  const [settings, setSettings] = useState<SettingsModel>(initialSettings);
  const ui = useUiTelemetry();
  const [status, setStatus] = useState<StatusMessage>({ text: isHosted ? 'Starting native audio engine…' : 'Interface preview — native engine not attached.', isError: false });
  const [alert, setAlert] = useState<StatusMessage | null>(null);
  const [history, setHistory] = useState<HistorySnapshot>({ canUndo: false, canRedo: false, undoLabel: '', redoLabel: '' });
  const [nodes, setNodes, onNodesChange] = useNodesState<RuseFlowNode>([]);
  const [zoom, setZoom] = useState(1);
  const [sidebarWidth, setSidebarWidth] = useState(() => {
    const saved = Number(localStorage.getItem('ruse.sidebarWidth'));
    return saved >= 240 && saved <= 460 ? saved : 304;
  });
  const [showSettings, setShowSettings] = useState(false);
  const [showHelp, setShowHelp] = useState(false);
  const [showClear, setShowClear] = useState(false);
  const [renamePrompt, setRenamePrompt] = useState<RenamePrompt | null>(null);
  const [routePrompt, setRoutePrompt] = useState<RoutePrompt | null>(null);
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
  const [connecting, setConnecting] = useState(false);
  const connectingRef = useRef(false);
  const pendingTelemetry = useRef<TelemetrySnapshot | null>(null);

  const nodesRef = useRef(nodes);
  const settingsRef = useRef(settings);
  const zCounter = useRef(1);
  useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  useEffect(() => { settingsRef.current = settings; }, [settings]);
  useEffect(() => { localStorage.setItem('ruse.sidebarWidth', String(sidebarWidth)); }, [sidebarWidth]);
  useEffect(() => { localStorage.setItem('ruse.libraryAliases', JSON.stringify(aliases)); }, [aliases]);

  useEffect(() => subscribe((type, payload) => {
    switch (type) {
      case 'catalog': setCatalog(payload as CatalogSnapshot); break;
      case 'graph': setGraph(payload as GraphSnapshot); break;
      case 'telemetry':
        if (connectingRef.current) pendingTelemetry.current = payload as TelemetrySnapshot;
        else publishTelemetry(payload as TelemetrySnapshot);
        break;
      case 'settings': setSettings(payload as SettingsModel); break;
      case 'history': setHistory(payload as HistorySnapshot); break;
      case 'chain-edit': setChainEditor(payload as ChainEditorRequest); break;
      case 'status': setStatus(payload as StatusMessage); break;
      case 'alert': setAlert(payload as StatusMessage); break;
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
    setNodes(graph.nodes.map(model => {
      const existing = current.get(model.id);
      return {
        id: model.id,
        type: 'ruseNode',
        position: { x: model.x, y: model.y },
        selected: existing?.selected ?? false,
        zIndex: existing?.zIndex ?? ++zCounter.current,
        data: {
          ...model,
          highlightMuted: settingsRef.current.highlightMutedNodes,
          transcriberModels: settingsRef.current.transcriberModels,
        },
      } satisfies RuseFlowNode;
    }));
  }, [graph.version, setNodes]);

  useEffect(() => {
    setNodes(current => current.map(node => node.data.isTranscriber
      ? { ...node, data: { ...node.data, transcriberModels: settings.transcriberModels } }
      : node));
  }, [settings.transcriberModels, setNodes]);

  useEffect(() => {
    const openSettings = () => setShowSettings(true);
    window.addEventListener('ruse-open-settings', openSettings);
    return () => window.removeEventListener('ruse-open-settings', openSettings);
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
      label: Math.abs(link.gain - 1) > 0.005 ? `${Math.round(link.gain * 100)}%` : undefined,
      style: { stroke: source?.accent ?? '#5B8DEF', strokeWidth: 2, opacity: 0.84 },
    };
  }), [graph.links, graph.nodes]);

  const sendAdd = (item: CatalogItem, position: { x: number; y: number }, linkId?: string) => {
    send('add', {
      kind: item.kind,
      id: item.id,
      title: aliases[catalogKey(item)]?.trim() || item.name,
      processId: item.processId ?? 0,
      micHint: item.micHint ?? '',
      linkId: linkId ?? '',
      x: Math.max(0, Math.round(position.x / 20) * 20),
      y: Math.max(0, Math.round(position.y / 20) * 20),
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
      width: node.measured?.width ?? 232,
      height: node.measured?.height ?? 140,
    }));
    return nearestFreePosition({ id: 'new', x: position.x, y: position.y, width: 232, height: 140 }, others);
  };

  const quickAdd = (item: CatalogItem) => {
    const bounds = graphElement.current?.getBoundingClientRect();
    const position = bounds
      ? flow.screenToFlowPosition({ x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 })
      : { x: 60 + graph.nodes.length * 30, y: 60 + graph.nodes.length * 30 };
    sendAdd(item, findFreeSpot({ x: position.x - 110, y: position.y - 60 }));
  };

  const tidyGraph = () => {
    const rank = { source: 0, modifier: 1, output: 2 };
    const columns = [80, 420, 760];
    const nextY = [80, 80, 80];
    const positions = [...nodesRef.current]
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
    const selected = nodesRef.current.find(node => node.selected);
    if (selected) send('duplicate', { id: selected.id });
  };

  const muteSelected = () => {
    const selected = nodesRef.current.find(node => node.selected);
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

  const finishConnecting = () => {
    connectingRef.current = false;
    setConnecting(false);
    if (pendingTelemetry.current) {
      publishTelemetry(pendingTelemetry.current);
      pendingTelemetry.current = null;
    }
  };

  const onNodeDragStop = (_event: unknown, node: RuseFlowNode) => {
    const others = nodesRef.current.map(item => ({
      id: item.id,
      x: item.position.x,
      y: item.position.y,
      width: item.measured?.width ?? 232,
      height: item.measured?.height ?? 120,
    }));
    const moving = others.find(item => item.id === node.id);
    if (!moving) return;
    const free = nearestFreePosition({ ...moving, x: node.position.x, y: node.position.y }, others);
    if (free.x !== node.position.x || free.y !== node.position.y) {
      setNodes(current => current.map(item => item.id === node.id ? { ...item, position: free } : item));
    }
    send('move', { id: node.id, x: free.x, y: free.y });
  };

  const onNodesDelete = (deleted: RuseFlowNode[]) => {
    for (const node of deleted) send('remove', { id: node.id });
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
        setShowHelp(true);
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
        searchRef.current?.focus();
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
          '2': 'Output devices',
          '3': 'Capture devices',
          '4': 'Virtual outputs',
          '5': 'Effects',
        };
        window.dispatchEvent(new CustomEvent('ruse-focus-section', { detail: { label: sections[event.key] } }));
      } else if (event.key === '?' || (event.key === '/' && event.shiftKey)) {
        event.preventDefault();
        setShowHelp(true);
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
    const move = (moveEvent: PointerEvent) => {
      setSidebarWidth(Math.min(460, Math.max(240, startWidth + moveEvent.clientX - startX)));
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  };

  const showMiniMap = nodes.length > 0 && settings.minimapMode !== 1 && (settings.minimapMode === 0 || zoom > 1.15);
  const running = ui.running || graph.running;
  const menuNode = canvasMenu?.kind === 'node' ? nodes.find(node => node.id === canvasMenu.id) : undefined;
  const menuLink = canvasMenu?.kind === 'edge' ? graph.links.find(link => link.id === canvasMenu.id) : undefined;
  const menuLeft = canvasMenu ? Math.min(canvasMenu.x, window.innerWidth - 230) : 0;
  const menuTop = canvasMenu ? Math.min(canvasMenu.y, window.innerHeight - 300) : 0;

  return (
    <div className={`app-shell ${settings.signalGlow ? 'signal-glow' : ''} ${settings.highlightCableOnHover ? 'cable-hover' : ''} ${connecting ? 'is-connecting' : ''}`}>
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
        onSettings={() => setShowSettings(true)}
        onHelp={() => setShowHelp(true)}
      />
      {alert && <div className={`alert-bar ${alert.isError ? 'error' : ''}`}><span>{alert.text}</span><div className="alert-actions"><button onClick={() => send('open-log')}>Open log</button><button onClick={() => setAlert(null)}>Dismiss</button></div></div>}
      <div className="workspace" style={{ gridTemplateColumns: `${sidebarWidth}px 5px minmax(0, 1fr)` }}>
        <Sidebar
          catalog={catalog}
          searchRef={searchRef}
          aliases={aliases}
          onAdd={quickAdd}
          onRename={setRenameLibrary}
          onNewChain={() => setChainEditor({ name: 'My chain', elements: [] })}
          onEditChain={item => setChainEditor({
            name: item.name,
            previousName: item.name,
            elements: (item.elements ?? []).map(element => ({ typeId: element.typeId, parameters: { ...element.parameters } })),
          })}
        />
        <div className="sidebar-resizer" role="separator" aria-label="Resize library" onPointerDown={startSidebarResize} />
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
              linkId ? { x: position.x - 116, y: position.y - 60 } : findFreeSpot({ x: position.x, y: position.y }),
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
            onConnectStart={() => { connectingRef.current = true; setConnecting(true); }}
            onConnectEnd={finishConnecting}
            connectOnClick
            connectionRadius={42}
            onNodeClick={(_event, node) => { bringNodeToFront(node.id); setCanvasMenu(null); }}
            onNodeDragStart={(_event, node) => bringNodeToFront(node.id)}
            onNodeDragStop={onNodeDragStop}
            onNodesDelete={onNodesDelete}
            onNodeDoubleClick={(_event, node) => {
              if (node.data.flavor === 'chain') {
                send('chain-open', { id: node.id });
              } else {
                setRenamePrompt({ id: node.id, title: node.data.title });
              }
            }}
            onNodeContextMenu={(event, node) => {
              event.preventDefault();
              setCanvasMenu({ x: event.clientX, y: event.clientY, kind: 'node', id: node.id });
            }}
            onEdgeDoubleClick={(_event, edge) => {
              const link = graph.links.find(item => item.id === edge.id);
              if (link?.kind === 'direct') setRoutePrompt({ id: link.id, gain: link.gain });
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
            fitView
            fitViewOptions={{ padding: 0.18 }}
            minZoom={0.25}
            maxZoom={2.5}
            snapToGrid
            snapGrid={[20, 20]}
            zIndexMode="manual"
            deleteKeyCode={['Delete', 'Backspace']}
            connectionLineStyle={{ stroke: '#65D6D0', strokeWidth: 2, strokeDasharray: '5 4' }}
            defaultEdgeOptions={{ type: 'ruseEdge' }}
          >
            <Background variant={BackgroundVariant.Dots} gap={24} size={1.15} color="#222B39" />
            <Panel position="top-left" className="graph-caption"><span>Routing canvas</span><strong>{nodes.length} nodes · {edges.length} cables</strong></Panel>
            <Panel position="bottom-left" className="canvas-controls">
              <button title="Zoom out" aria-label="Zoom out" onClick={() => flow.zoomOut({ duration: 120 })}><Minus size={14} /></button>
              <button title="Reset zoom" aria-label="Reset zoom" onClick={() => flow.zoomTo(1, { duration: 160 })}>{Math.round(zoom * 100)}%</button>
              <button title="Zoom in" aria-label="Zoom in" onClick={() => flow.zoomIn({ duration: 120 })}><Plus size={14} /></button>
              <button title="Fit graph (F)" aria-label="Fit graph" onClick={fitGraph}><Maximize2 size={14} /></button>
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
            <span className="eyebrow">Start with a signal</span>
            <h2>Build your first route</h2>
            <p>Add a source and an output, then drag from OUT to IN. Processing nodes sit between them.</p>
            <div className="empty-steps"><span><b>1</b> Source</span><span><b>2</b> Processing</span><span><b>3</b> Destination</span></div>
            <button className="primary-button" onClick={() => quickAdd(systemItem)}>Add system audio</button>
            <small>Or drag anything from the library onto the canvas.</small>
          </div>}
        </main>
      </div>
      <StatusBar
        status={status}
        zoom={zoom}
        onZoomReset={() => flow.zoomTo(1, { duration: 160 })}
      />
      {showSettings && <SettingsDialog settings={settings} onChange={next => { setSettings(next); send('settings', { ...next }); }} onClose={() => setShowSettings(false)} />}
      {showHelp && <HelpDialog onClose={() => setShowHelp(false)} />}
      {showClear && <ConfirmDialog title="Clear routing graph" body="This removes every node and cable. The current session will be replaced when you close Ruse." confirmLabel="Clear graph" onConfirm={() => { setShowClear(false); send('clear'); }} onClose={() => setShowClear(false)} />}
      {renamePrompt && <PromptDialog title="Rename node" label="Node name" value={renamePrompt.title} submitLabel="Rename" onSubmit={value => { send('rename', { id: renamePrompt.id, title: value }); setRenamePrompt(null); }} onClose={() => setRenamePrompt(null)} />}
      {routePrompt && <PromptDialog title="Direct route gain" label="Gain (0–200%)" value={String(Math.round(routePrompt.gain * 100))} submitLabel="Set gain" onSubmit={value => { const gain = Number(value) / 100; if (Number.isFinite(gain)) send('route-gain', { id: routePrompt.id, gain: Math.min(2, Math.max(0, gain)) }); setRoutePrompt(null); }} onClose={() => setRoutePrompt(null)} />}
      {renameLibrary && <PromptDialog
        title="Rename library item"
        label="Display name"
        value={aliases[catalogKey(renameLibrary)] ?? renameLibrary.name}
        submitLabel="Rename"
        onSubmit={value => {
          setAliases(current => ({ ...current, [catalogKey(renameLibrary)]: value }));
          setRenameLibrary(null);
        }}
        onClose={() => setRenameLibrary(null)}
      />}
      {chainEditor && <ChainEditorDialog
        request={chainEditor}
        effects={catalog.effects}
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
      {canvasMenu && <div className="context-layer" onPointerDown={event => { if (event.target === event.currentTarget) setCanvasMenu(null); }}>
        <div className="context-menu" role="menu" style={{ left: menuLeft, top: menuTop }}>
          {canvasMenu.kind === 'node' && menuNode && <>
            <button onClick={() => { setRenamePrompt({ id: menuNode.id, title: menuNode.data.title }); setCanvasMenu(null); }}><Pencil size={14} /> Rename</button>
            <button onClick={() => { send('duplicate', { id: menuNode.id }); setCanvasMenu(null); }}><Copy size={14} /> Duplicate <kbd>Ctrl D</kbd></button>
            <button onClick={() => { send('mute', { id: menuNode.id, value: !menuNode.data.muted }); setCanvasMenu(null); }}>{menuNode.data.muted ? <Volume2 size={14} /> : <VolumeX size={14} />} {menuNode.data.muted ? 'Unmute node' : 'Mute node'} <kbd>M</kbd></button>
            <button onClick={() => { send('disconnect-node', { id: menuNode.id }); setCanvasMenu(null); }}><Unplug size={14} /> Disconnect routes</button>
            <div className="menu-separator" />
            <button className="danger" onClick={() => { send('remove', { id: menuNode.id }); setCanvasMenu(null); }}><Trash2 size={14} /> Remove node <kbd>Del</kbd></button>
          </>}
          {canvasMenu.kind === 'edge' && menuLink && <>
            {menuLink.kind === 'direct' && <button onClick={() => { setRoutePrompt({ id: menuLink.id, gain: menuLink.gain }); setCanvasMenu(null); }}>Set route gain…</button>}
            <button className="danger" onClick={() => { send('disconnect', { id: menuLink.id }); setCanvasMenu(null); }}><Unplug size={14} /> Disconnect cable</button>
          </>}
          {canvasMenu.kind === 'pane' && <>
            <button onClick={() => {
              if (canvasMenu.flowX != null && canvasMenu.flowY != null) sendAdd(systemItem, findFreeSpot({ x: canvasMenu.flowX, y: canvasMenu.flowY }));
              setCanvasMenu(null);
            }}><Plus size={14} /> Add system audio</button>
            <button onClick={() => { fitGraph(); setCanvasMenu(null); }}><Maximize2 size={14} /> Fit graph <kbd>F</kbd></button>
            <button onClick={tidyGraph}><LayoutGrid size={14} /> Tidy up</button>
            <div className="menu-separator" />
            <button className="danger" onClick={() => { setShowClear(true); setCanvasMenu(null); }}><Trash2 size={14} /> Clear graph</button>
          </>}
        </div>
      </div>}
    </div>
  );
}
