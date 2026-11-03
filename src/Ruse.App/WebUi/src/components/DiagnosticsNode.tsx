import { useCallback, useEffect, useRef, useState } from 'react';
import type { CSSProperties, PointerEvent as ReactPointerEvent, ReactNode } from 'react';
import { useStore, useUpdateNodeInternals } from '@xyflow/react';
import { Activity, X } from 'lucide-react';
import { loadDiagnosticsLayout, saveDiagnosticsLayout } from '../diagnostics';
import { useI18n } from '../i18n';
import { useGlobalTelemetry } from '../telemetry';
import type { DiagnosticInfo } from '../types';

const DASH = '\u2014';
const HISTORY_POINTS = 240; // 60 seconds sampled at 4 Hz
const HISTORY_SAMPLE_MS = 250;
const GRAPH_WIDTH = 180;
const GRAPH_HEIGHT = 26;

function formatMb(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? `${value.toFixed(value >= 100 ? 0 : 1)} MB`
    : DASH;
}

function formatSigned(value: number, digits = 1): string {
  if (!Number.isFinite(value)) return DASH;
  return `${value >= 0 ? '+' : ''}${value.toFixed(digits)}`;
}

function formatMs(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? `${value.toFixed(value >= 100 ? 0 : 1)} ms`
    : DASH;
}

function metricValue(
  key: string,
  diagnostics: DiagnosticInfo | null | undefined,
  fps: number,
  frameMs: number,
  heapMb: number | null,
): number | null {
  switch (key) {
    case 'sources': return diagnostics?.sourcesRunning ?? null;
    case 'outputs': return diagnostics?.outputsRunning ?? null;
    case 'latency': return diagnostics?.worstLatencyMs ?? null;
    case 'engine-block': return diagnostics?.engineBlockMs ?? null;
    case 'output-cushion': return diagnostics?.outputTargetMs ?? null;
    case 'endpoint-latency': return diagnostics?.endpointLatencyMs ?? null;
    case 'render-buffer': return diagnostics?.outputBufferedMs ?? null;
    case 'engine-queue': return diagnostics?.engineQueueMs ?? null;
    case 'capture-backlog': return diagnostics?.worstBacklogMs ?? null;
    case 'source-fill': return diagnostics?.sourceFillRateMsPerSec ?? null;
    case 'output-ring': return diagnostics?.outputRingFillPercent ?? null;
    case 'underruns': return diagnostics?.underrunsPerMinute ?? null;
    case 'skips': return diagnostics?.skippedFramesPerMinute ?? null;
    case 'latency-mean': return diagnostics?.latencyMeanMs ?? null;
    case 'latency-stddev': return diagnostics?.latencyStdDevMs ?? null;
    case 'drift': return diagnostics?.estimatedDriftPpm ?? null;
    case 'chain-delay': return diagnostics?.chainLatencyMs ?? null;
    case 'skipped-frames': return diagnostics?.skippedFrames ?? null;
    case 'short-read-frames': return diagnostics?.shortReadFrames ?? null;
    case 'output-underruns': return diagnostics?.outputUnderruns ?? null;
    case 'cpu': return diagnostics?.cpuPercent ?? null;
    case 'cpu-share': return diagnostics?.cpuShare ?? null;
    case 'gpu': return diagnostics?.gpuPercent ?? null;
    case 'render-tier': return diagnostics?.renderTier ?? null;
    case 'memory': return diagnostics?.workingSetMb ?? null;
    case 'managed': return diagnostics?.managedMb ?? null;
    case 'threads': return diagnostics?.threads ?? null;
    case 'telemetry': return diagnostics?.telemetryMs ?? null;
    case 'fps': return fps > 0 ? fps : null;
    case 'frame': return frameMs > 0 ? frameMs : null;
    case 'heap': return heapMb;
    case 'nodes': return diagnostics?.nodeCount ?? null;
    case 'links': return diagnostics?.linkCount ?? null;
    case 'uptime': return diagnostics?.processUptimeSeconds ?? null;
    default: return null;
  }
}

function Sparkline({ values, color }: { values: number[]; color: string }) {
  if (values.length < 2) {
    return (
      <svg className="diag-spark-svg" viewBox={`0 0 ${GRAPH_WIDTH} ${GRAPH_HEIGHT}`} preserveAspectRatio="none">
        <line x1={0} y1={GRAPH_HEIGHT - 1} x2={GRAPH_WIDTH} y2={GRAPH_HEIGHT - 1} style={{ stroke: 'rgba(255,255,255,0.12)' }} strokeWidth={1} />
      </svg>
    );
  }

  let min = values[0];
  let max = values[0];
  for (const value of values) {
    if (value < min) min = value;
    if (value > max) max = value;
  }

  if (max - min < 1e-9) {
    const pad = Math.max(1, Math.abs(max) * 0.05);
    min -= pad;
    max += pad;
  }

  let path = '';
  for (let index = 0; index < values.length; index++) {
    const x = (index / (HISTORY_POINTS - 1)) * GRAPH_WIDTH;
    const y = GRAPH_HEIGHT - ((values[index] - min) / (max - min)) * GRAPH_HEIGHT;
    path += `${index === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`;
  }

  return (
    <svg className="diag-spark-svg" viewBox={`0 0 ${GRAPH_WIDTH} ${GRAPH_HEIGHT}`} preserveAspectRatio="none">
      <path d={path} fill="none" style={{ stroke: color }} strokeWidth={1.2} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

function DiagRow({
  label,
  value,
  metric,
  color,
  activeGraphs,
  onToggle,
  histories,
}: {
  label: string;
  value: ReactNode;
  metric?: string;
  color: string;
  activeGraphs: string[];
  onToggle: (metric: string) => void;
  histories: Record<string, number[]>;
}) {
  const active = metric ? activeGraphs.includes(metric) : false;
  return (
    <div className={`diag-row ${active ? 'graphed' : ''}`}>
      {metric ? (
        <button
          type="button"
          className={`diag-graph-button nodrag ${active ? 'active' : ''}`}
          title={label}
          aria-label={label}
          aria-pressed={active}
          onPointerDown={event => event.stopPropagation()}
          onClick={() => onToggle(metric)}
        >
          <Activity size={9} />
        </button>
      ) : <span className="diag-graph-spacer" aria-hidden="true" />}
      <span>{label}</span>
      <b>{value}</b>
      {active && metric && (
        <div className="diag-spark">
          <Sparkline values={histories[metric] ?? []} color={color} />
        </div>
      )}
    </div>
  );
}

export default function DiagnosticsNode({ nodeId }: { nodeId: string }) {
  const { t } = useI18n();
  const telemetry = useGlobalTelemetry();
  const diagnostics = telemetry.diagnostics;
  const updateNodeInternals = useUpdateNodeInternals();
  const nodeCount = useStore(state =>
    state.nodes.filter(node => !(node.data as { diagnostics?: boolean }).diagnostics).length);
  const linkCount = useStore(state => state.edges.length);
  const rootRef = useRef<HTMLDivElement>(null);
  const [scale, setScale] = useState(() => loadDiagnosticsLayout().scale);
  const [scaling, setScaling] = useState(false);
  const [fps, setFps] = useState(0);
  const [frameMs, setFrameMs] = useState(0);
  const [heapMb, setHeapMb] = useState<number | null>(null);
  const [activeGraphs, setActiveGraphs] = useState<string[]>([]);
  const histories = useRef<Record<string, number[]>>({});
  const lastSample = useRef(0);

  const toggleGraph = useCallback((metric: string) => {
    setActiveGraphs(current => current.includes(metric)
      ? current.filter(item => item !== metric)
      : [...current, metric]);
  }, []);

  useEffect(() => {
    if (activeGraphs.length === 0) return;
    const now = performance.now();
    if (now - lastSample.current < HISTORY_SAMPLE_MS) return;
    lastSample.current = now;

    for (const metric of activeGraphs) {
      const value = metricValue(metric, diagnostics, fps, frameMs, heapMb);
      if (value === null || !Number.isFinite(value)) {
        continue;
      }

      const values = histories.current[metric] ?? (histories.current[metric] = []);
      values.push(value);
      if (values.length > HISTORY_POINTS) {
        values.splice(0, values.length - HISTORY_POINTS);
      }
    }
  }, [activeGraphs, diagnostics, fps, frameMs, heapMb]);

  useEffect(() => {
    updateNodeInternals(nodeId);
    const timer = window.setTimeout(() => {
      saveDiagnosticsLayout({ ...loadDiagnosticsLayout(), scale });
    }, 250);
    return () => window.clearTimeout(timer);
  }, [scale, nodeId, updateNodeInternals]);

  useEffect(() => {
    updateNodeInternals(nodeId);
  }, [activeGraphs, nodeId, updateNodeInternals]);

  useEffect(() => {
    let frame = 0;
    let frames = 0;
    let elapsed = 0;
    let last = performance.now();
    const tick = (now: number) => {
      frames++;
      elapsed += now - last;
      last = now;
      if (elapsed >= 500) {
        setFps(frames * 1000 / elapsed);
        setFrameMs(elapsed / frames);
        const memory = (performance as Performance & { memory?: { usedJSHeapSize: number } }).memory;
        setHeapMb(memory ? memory.usedJSHeapSize / (1024 * 1024) : null);
        frames = 0;
        elapsed = 0;
      }
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, []);

  const startScaling = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    const root = rootRef.current;
    if (!root) return;

    const rect = root.getBoundingClientRect();
    const originX = event.clientX;
    const startScale = scale;
    const startWidth = Math.max(70, rect.width);

    event.preventDefault();
    event.stopPropagation();
    setScaling(true);

    const move = (moveEvent: PointerEvent) => {
      const ratio = (startWidth + moveEvent.clientX - originX) / startWidth;
      setScale(Math.min(2, Math.max(0.6, startScale * ratio)));
    };

    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      setScaling(false);
    };

    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
  };

  const cpu = diagnostics
    ? `${diagnostics.cpuPercent.toFixed(1)}% ${t('core')} ${DASH} ${diagnostics.cpuShare.toFixed(2)}% ${t('total')}`
    : DASH;
  const gpu = diagnostics
    ? diagnostics.gpuPercent == null
      ? `${t('tier')} ${diagnostics.renderTier}`
      : `${diagnostics.gpuPercent.toFixed(0)}% ${DASH} ${t('tier')} ${diagnostics.renderTier}`
    : DASH;
  const rowProps = {
    activeGraphs,
    onToggle: toggleGraph,
    histories: histories.current,
  };

  return (
    <div
      ref={rootRef}
      className={`diagnostics-node ${scaling ? 'scaling' : ''}`}
      style={{ zoom: scale } as CSSProperties}
    >
      <header>
        <Activity size={12} />
        <strong>{t('Diagnostics')}</strong>
        <button
          className="diag-close nodrag"
          title={t('Hide diagnostics')}
          aria-label={t('Hide diagnostics')}
          onPointerDown={event => event.stopPropagation()}
          onClick={() => window.dispatchEvent(new CustomEvent('ruse-close-diagnostics'))}
        >
          <X size={13} />
        </button>
      </header>
      <div className="diagnostics-body">
        <div className="diag-section">
          <span className="diag-title">{t('Audio')}</span>
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Sources')} value={diagnostics ? `${diagnostics.sourcesRunning}/${diagnostics.sourceCount}` : DASH} metric="sources" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Outputs')} value={diagnostics ? `${diagnostics.outputsRunning}/${diagnostics.outputCount}` : DASH} metric="outputs" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Latency')} value={formatMs(diagnostics?.worstLatencyMs)} metric="latency" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Engine block')} value={formatMs(diagnostics?.engineBlockMs)} metric="engine-block" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Output cushion')} value={formatMs(diagnostics?.outputTargetMs)} metric="output-cushion" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Endpoint latency')} value={formatMs(diagnostics?.endpointLatencyMs)} metric="endpoint-latency" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Render buffer')} value={formatMs(diagnostics?.outputBufferedMs)} metric="render-buffer" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Engine queue')} value={formatMs(diagnostics?.engineQueueMs)} metric="engine-queue" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Capture backlog')} value={formatMs(diagnostics?.worstBacklogMs)} metric="capture-backlog" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Source fill')} value={diagnostics ? `${formatSigned(diagnostics.sourceFillRateMsPerSec)} ms/s` : DASH} metric="source-fill" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Output ring')} value={diagnostics ? `${diagnostics.outputRingFillPercent.toFixed(1)}%` : DASH} metric="output-ring" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Underruns/min')} value={diagnostics ? diagnostics.underrunsPerMinute.toFixed(1) : DASH} metric="underruns" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Skips/min')} value={diagnostics ? diagnostics.skippedFramesPerMinute.toFixed(1) : DASH} metric="skips" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Latency mean')} value={diagnostics ? `${formatMs(diagnostics.latencyMeanMs)} ${DASH} ${formatMs(diagnostics.latencyStdDevMs)}` : DASH} metric="latency-mean" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Estimated drift')} value={diagnostics ? `${formatSigned(diagnostics.estimatedDriftPpm)} ppm` : DASH} metric="drift" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Chain delay')} value={formatMs(diagnostics?.chainLatencyMs)} metric="chain-delay" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Skipped / underrun')} value={diagnostics ? `${diagnostics.skippedFrames} / ${diagnostics.shortReadFrames}` : DASH} metric="short-read-frames" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Output underruns')} value={diagnostics?.outputUnderruns ?? DASH} metric="output-underruns" />
          <DiagRow {...rowProps} color="var(--cyan)" label={t('Format')} value={diagnostics?.audioFormat ?? '48 kHz \u00b7 stereo \u00b7 f32'} />
        </div>
        <div className="diag-section">
          <span className="diag-title">{t('Performance')}</span>
          <DiagRow {...rowProps} color="var(--amber)" label="CPU" value={cpu} metric="cpu" />
          <DiagRow {...rowProps} color="var(--amber)" label="GPU" value={gpu} metric={diagnostics?.gpuPercent != null ? 'gpu' : undefined} />
          <DiagRow {...rowProps} color="var(--amber)" label={t('GPU device')} value={diagnostics ? (diagnostics.gpuAvailable ? diagnostics.gpuDeviceName ?? DASH : diagnostics.gpuStatusError ?? DASH) : DASH} />
          {diagnostics?.gpuFallbackReason && <div className="diag-row"><span className="diag-graph-spacer" aria-hidden="true" /><span>{t('GPU fallback')}</span><b>{diagnostics.gpuFallbackReason}</b></div>}
          <DiagRow {...rowProps} color="var(--amber)" label={t('Memory')} value={formatMb(diagnostics?.workingSetMb)} metric="memory" />
          <DiagRow {...rowProps} color="var(--amber)" label={t('Managed')} value={formatMb(diagnostics?.managedMb)} metric="managed" />
          <DiagRow {...rowProps} color="var(--amber)" label={t('Threads')} value={diagnostics?.threads ?? DASH} metric="threads" />
          <DiagRow {...rowProps} color="var(--amber)" label={t('Telemetry tick')} value={formatMs(diagnostics?.telemetryMs)} metric="telemetry" />
        </div>
        <div className="diag-section">
          <span className="diag-title">{t('Interface')}</span>
          <DiagRow {...rowProps} color="var(--signal)" label="FPS" value={fps ? fps.toFixed(0) : DASH} metric="fps" />
          <DiagRow {...rowProps} color="var(--signal)" label={t('Frame')} value={frameMs ? frameMs.toFixed(1) + ' ms' : DASH} metric="frame" />
          <DiagRow {...rowProps} color="var(--signal)" label="JS heap" value={formatMb(heapMb)} metric="heap" />
          <DiagRow {...rowProps} color="var(--signal)" label={t('Graph')} value={`${nodeCount} / ${linkCount}`} metric="nodes" />
          <DiagRow {...rowProps} color="var(--signal)" label={t('Uptime')} value={diagnostics ? `${Math.floor(diagnostics.processUptimeSeconds / 60)}m ${Math.floor(diagnostics.processUptimeSeconds % 60)}s` : DASH} metric="uptime" />
        </div>
      </div>
      <div
        className="diag-resize nodrag"
        title={t('Drag to scale')}
        onPointerDown={startScaling}
      />
    </div>
  );
}
