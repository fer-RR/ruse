import { useEffect, useRef, useState } from 'react';
import type { CSSProperties, PointerEvent as ReactPointerEvent } from 'react';
import { useStore, useUpdateNodeInternals } from '@xyflow/react';
import { Activity, X } from 'lucide-react';
import { loadDiagnosticsLayout, saveDiagnosticsLayout } from '../diagnostics';
import { useI18n } from '../i18n';
import { useGlobalTelemetry } from '../telemetry';

function formatMb(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? `${value.toFixed(value >= 100 ? 0 : 1)} MB`
    : '—';
}

function formatMs(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? `${value.toFixed(value >= 100 ? 0 : 1)} ms`
    : '—';
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

  useEffect(() => {
    updateNodeInternals(nodeId);
    const timer = window.setTimeout(() => {
      saveDiagnosticsLayout({ ...loadDiagnosticsLayout(), scale });
    }, 250);
    return () => window.clearTimeout(timer);
  }, [scale, nodeId, updateNodeInternals]);

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
    ? `${diagnostics.cpuPercent.toFixed(1)}% ${t('core')} · ${diagnostics.cpuShare.toFixed(2)}% ${t('total')}`
    : '—';
  const gpu = diagnostics
    ? diagnostics.gpuPercent == null
      ? `${t('tier')} ${diagnostics.renderTier}`
      : `${diagnostics.gpuPercent.toFixed(0)}% · ${t('tier')} ${diagnostics.renderTier}`
    : '—';

  return (
    <div
      ref={rootRef}
      className={`diagnostics-node ${scaling ? 'scaling' : ''}`}
      style={{ zoom: scale } as CSSProperties}
    >
      <header>
        <Activity size={12} />
        <strong>{t('Diagnostics')}</strong>
        <span>LIVE · {Math.round(scale * 100)}%</span>
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
          <div className="diag-row"><span>{t('Sources')}</span><b>{diagnostics ? `${diagnostics.sourcesRunning}/${diagnostics.sourceCount}` : '—'}</b></div>
          <div className="diag-row"><span>{t('Outputs')}</span><b>{diagnostics ? `${diagnostics.outputsRunning}/${diagnostics.outputCount}` : '—'}</b></div>
          <div className="diag-row"><span>{t('Latency')}</span><b>{formatMs(diagnostics?.worstLatencyMs)}</b></div>
          <div className="diag-row"><span>{t('Capture backlog')}</span><b>{formatMs(diagnostics?.worstBacklogMs)}</b></div>
          <div className="diag-row"><span>{t('Chain delay')}</span><b>{formatMs(diagnostics?.chainLatencyMs)}</b></div>
          <div className="diag-row"><span>{t('Skipped / underrun')}</span><b>{diagnostics ? `${diagnostics.skippedFrames} / ${diagnostics.shortReadFrames}` : '—'}</b></div>
          <div className="diag-row"><span>{t('Output underruns')}</span><b>{diagnostics?.outputUnderruns ?? '—'}</b></div>
          <div className="diag-row"><span>{t('Format')}</span><b>{diagnostics?.audioFormat ?? '48 kHz · stereo · f32'}</b></div>
        </div>
        <div className="diag-section">
          <span className="diag-title">{t('Performance')}</span>
          <div className="diag-row"><span>CPU</span><b>{cpu}</b></div>
          <div className="diag-row"><span>GPU</span><b>{gpu}</b></div>
          <div className="diag-row"><span>{t('GPU device')}</span><b>{diagnostics ? (diagnostics.gpuAvailable ? diagnostics.gpuDeviceName ?? '—' : diagnostics.gpuStatusError ?? '—') : '—'}</b></div>
          {diagnostics?.gpuFallbackReason && <div className="diag-row"><span>{t('GPU fallback')}</span><b>{diagnostics.gpuFallbackReason}</b></div>}
          <div className="diag-row"><span>{t('Memory')}</span><b>{formatMb(diagnostics?.workingSetMb)}</b></div>
          <div className="diag-row"><span>{t('Managed')}</span><b>{formatMb(diagnostics?.managedMb)}</b></div>
          <div className="diag-row"><span>{t('Threads')}</span><b>{diagnostics?.threads ?? '—'}</b></div>
          <div className="diag-row"><span>{t('Telemetry tick')}</span><b>{formatMs(diagnostics?.telemetryMs)}</b></div>
        </div>
        <div className="diag-section">
          <span className="diag-title">{t('Interface')}</span>
          <div className="diag-row"><span>FPS</span><b>{fps ? fps.toFixed(0) : '—'}</b></div>
          <div className="diag-row"><span>{t('Frame')}</span><b>{frameMs ? frameMs.toFixed(1) + ' ms' : '—'}</b></div>
          <div className="diag-row"><span>JS heap</span><b>{formatMb(heapMb)}</b></div>
          <div className="diag-row"><span>{t('Graph')}</span><b>{nodeCount} / {linkCount}</b></div>
          <div className="diag-row"><span>{t('Uptime')}</span><b>{diagnostics ? `${Math.floor(diagnostics.processUptimeSeconds / 60)}m ${Math.floor(diagnostics.processUptimeSeconds % 60)}s` : '—'}</b></div>
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
