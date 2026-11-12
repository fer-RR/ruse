import { useEffect, useMemo, useRef, useState } from 'react';
import type {
  TimelineAutomationLaneModel,
  TimelineAutomationTarget,
  TimelineClipModel,
  TimelineState,
  TimelineTrackModel,
} from '../timeline';
import { useI18n } from '../i18n';
import { formatGainDb, nextVolumeValue } from '../gain';
import { withShortcut } from '../shortcuts';
import { ConfirmDialog } from './Dialogs';

const BASE_PIXELS_PER_SECOND = 80;
const MAX_CANVAS_WIDTH = 16000;
const MIN_ZOOM = 0.001;
const MAX_ZOOM = 4;
const RULER_HEIGHT = 22;
const TRACK_HEIGHT = 72;
const AUTOMATION_HEIGHT = 34;

type DragKind = 'move' | 'resize-left' | 'resize-right' | 'stretch-right' | 'fade-in' | 'fade-out';

interface TimelineRow {
  kind: 'track' | 'automation';
  index: number;
  track: TimelineTrackModel;
  lane?: TimelineAutomationLaneModel;
  y: number;
  height: number;
}

interface ClipPreview {
  startSample: number;
  lengthSamples: number;
  sourceOffsetSamples: number;
  fadeInSamples: number;
  fadeOutSamples: number;
}

interface DragState {
  kind: DragKind;
  trackId: string;
  clipId: string;
  targetTrackId: string;
  pointerStartX: number;
  original: ClipPreview;
  preview: ClipPreview;
}

interface AutomationDrag {
  trackId: string;
  laneId: string;
  pointIndex: number;
  pointerStartX: number;
  originalPoints: { sample: number; value: number; curve: number }[];
  points: { sample: number; value: number; curve: number }[];
}

type PendingRemoval =
  | { kind: 'clip'; trackId: string; clipId: string }
  | { kind: 'automation'; trackId: string; laneId: string }
  | { kind: 'track'; trackId: string };

export interface TimelineClipUpdate {
  startSample?: number;
  lengthSamples?: number;
  sourceOffsetSamples?: number;
  gain?: number;
  fadeInSamples?: number;
  fadeOutSamples?: number;
}

function TrackGainSlider({ track, onChange }: { track: TimelineTrackModel; onChange: (gain: number) => void }) {
  const [value, setValue] = useState(track.gain);
  const [dragging, setDragging] = useState(false);
  const dragTimer = useRef<number | null>(null);
  const pending = useRef<number | null>(null);
  const sendTimer = useRef<number | null>(null);
  const onChangeRef = useRef(onChange);

  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  useEffect(() => {
    if (!dragging) setValue(track.gain);
  }, [dragging, track.gain]);

  useEffect(() => () => {
    if (dragTimer.current !== null) window.clearTimeout(dragTimer.current);
    if (sendTimer.current !== null) window.clearTimeout(sendTimer.current);
    if (pending.current !== null) onChangeRef.current(pending.current);
  }, []);

  const queue = (next: number) => {
    pending.current = next;
    if (sendTimer.current === null) {
      sendTimer.current = window.setTimeout(() => {
        sendTimer.current = null;
        if (pending.current !== null) {
          onChangeRef.current(pending.current);
          pending.current = null;
        }
      }, 50);
    }
  };

  return (
    <span className="timeline-track-gain">
      <input
        type="range"
        min={0}
        max={1.5}
        step={0.01}
        value={value}
        aria-label={`${track.name} volume`}
        aria-valuetext={formatGainDb(value)}
        onPointerDown={() => {
          if (dragTimer.current !== null) window.clearTimeout(dragTimer.current);
          setDragging(true);
        }}
        onPointerUp={() => {
          if (dragTimer.current !== null) window.clearTimeout(dragTimer.current);
          dragTimer.current = window.setTimeout(() => {
            dragTimer.current = null;
            setDragging(false);
          }, 180);
        }}
        onPointerCancel={() => setDragging(false)}
        onChange={event => {
          const next = Number(event.target.value);
          setValue(next);
          queue(next);
        }}
        onDoubleClick={() => {
          setValue(1);
          queue(1);
        }}
        onKeyDown={event => {
          const next = nextVolumeValue(event, value);
          if (next !== null) {
            event.preventDefault();
            setValue(next);
            queue(next);
          }
        }}
      />
      <small className="volume-db">{formatGainDb(value)}</small>
    </span>
  );
}

export default function TimelinePanel({
  state,
  automationTargets,
  nodeIds,
  onPlay,
  onStop,
  onToggleRecord,
  onAddTrack,
  onImport,
  onSeek,
  onAddClipPath,
  onMoveClip,
  onMoveClipToTrack,
  onUpdateClip,
  onStretchClip,
  onRemoveClip,
  onSetLoop,
  onSetTempo,
  onBounce,
  onFreeze,
  onTrackUpdate,
  onAddAutomation,
  onSetAutomationPoints,
  onRemoveAutomation,
  onRemoveTrack,
  onShowTrackNode,
  onUndo,
  onRedo,
  onClose,
}: {
  state: TimelineState;
  automationTargets: TimelineAutomationTarget[];
  nodeIds: Set<string>;
  onPlay: () => void;
  onStop: () => void;
  onToggleRecord: () => void;
  onAddTrack: () => void;
  onImport: () => void;
  onSeek: (sample: number) => void;
  onAddClipPath: (trackId: string, path: string, startSample: number) => void;
  onMoveClip: (trackId: string, clipId: string, startSample: number) => void;
  onMoveClipToTrack: (fromTrackId: string, toTrackId: string, clipId: string, startSample: number) => void;
  onUpdateClip: (trackId: string, clipId: string, update: TimelineClipUpdate) => void;
  onStretchClip: (trackId: string, clipId: string, lengthSamples: number) => void;
  onRemoveClip: (trackId: string, clipId: string) => void;
  onSetLoop: (startSample: number | null, endSample: number | null) => void;
  onSetTempo: (tempo: number) => void;
  onBounce: () => void;
  onFreeze: () => void;
  onTrackUpdate: (trackId: string, gain: number, muted: boolean, solo: boolean) => void;
  onAddAutomation: (trackId: string, target: TimelineAutomationTarget) => void;
  onSetAutomationPoints: (trackId: string, laneId: string, points: { sample: number; value: number; curve: number }[]) => void;
  onRemoveAutomation: (trackId: string, laneId: string) => void;
  onRemoveTrack: (trackId: string) => void;
  onShowTrackNode: (trackId: string, sourceNodeId?: string) => void;
  onUndo: () => void;
  onRedo: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const panRef = useRef<{ clientX: number; scrollLeft: number } | null>(null);
  const [zoom, setZoom] = useState(1);
  const [panning, setPanning] = useState(false);
  const [seeking, setSeeking] = useState(false);
  const [dropActive, setDropActive] = useState(false);
  const [selectedClip, setSelectedClip] = useState<{ trackId: string; clipId: string } | null>(null);
  const [snap, setSnap] = useState('0.1');
  const [tempoDraft, setTempoDraft] = useState('120');
  const [drag, setDrag] = useState<DragState | null>(null);
  const [automationDrag, setAutomationDrag] = useState<AutomationDrag | null>(null);
  const [pendingRemoval, setPendingRemoval] = useState<PendingRemoval | null>(null);

  const tracks = state.project.tracks;
  const sampleRate = state.sampleRate;
  const selectedClipModel = useMemo(() => {
    if (!selectedClip) return null;
    const track = tracks.find(item => item.id === selectedClip.trackId);
    const clip = track?.clips.find(item => item.id === selectedClip.clipId);
    return track && clip ? { track, clip } : null;
  }, [selectedClip, tracks]);
  const [clipGain, setClipGain] = useState(1);
  const clipGainTimer = useRef<number | null>(null);
  const clipGainPending = useRef<number | null>(null);
  const clipGainDirtyUntil = useRef(0);
  useEffect(() => {
    if (selectedClipModel && Date.now() >= clipGainDirtyUntil.current) {
      setClipGain(selectedClipModel.clip.gain);
    }
  }, [selectedClipModel?.clip.id, selectedClipModel?.clip.gain]);
  useEffect(() => () => {
    if (clipGainTimer.current !== null) window.clearTimeout(clipGainTimer.current);
  }, []);
  useEffect(() => {
    if (clipGainTimer.current !== null) {
      window.clearTimeout(clipGainTimer.current);
      clipGainTimer.current = null;
    }
    clipGainPending.current = null;
    clipGainDirtyUntil.current = 0;
  }, [selectedClip?.clipId]);
  const queueClipGain = (value: number, immediate = false) => {
    if (!selectedClipModel) return;
    clipGainDirtyUntil.current = Date.now() + 400;
    setClipGain(value);
    clipGainPending.current = value;
    const trackId = selectedClipModel.track.id;
    const clipId = selectedClipModel.clip.id;
    const send = () => {
      const pending = clipGainPending.current;
      clipGainTimer.current = null;
      if (pending !== null) {
        onUpdateClip(trackId, clipId, { gain: pending });
      }
    };
    if (immediate) {
      if (clipGainTimer.current !== null) window.clearTimeout(clipGainTimer.current);
      send();
    } else if (clipGainTimer.current === null) {
      clipGainTimer.current = window.setTimeout(send, 50);
    }
  };
  const rows = useMemo(() => {
    const result: TimelineRow[] = [];
    let rowY = RULER_HEIGHT;
    for (let index = 0; index < state.project.tracks.length; index++) {
      const track = state.project.tracks[index];
      result.push({ kind: 'track', index, track, y: rowY, height: TRACK_HEIGHT });
      rowY += TRACK_HEIGHT;
      for (const lane of track.automation ?? []) {
        result.push({ kind: 'automation', index, track, lane, y: rowY, height: AUTOMATION_HEIGHT });
        rowY += AUTOMATION_HEIGHT;
      }
    }

    return result;
  }, [state.project.tracks]);

  const maxEnd = Math.max(
    sampleRate * 2,
    tracks.reduce((max, track) => track.clips.reduce((clipMax, clip) =>
      Math.max(clipMax, clip.startSample + clip.lengthSamples), max), 0),
    tracks.reduce((max, track) => (track.automation ?? []).reduce((laneMax, lane) =>
      lane.points.reduce((pointMax, point) => Math.max(pointMax, point.sample), laneMax), max), 0),
  );
  const seconds = Math.max(12, maxEnd / sampleRate + 2);
  const pixelsPerSecond = Math.min(BASE_PIXELS_PER_SECOND * zoom, MAX_CANVAS_WIDTH / seconds);
  const width = Math.max(320, Math.ceil(seconds * pixelsPerSecond));
  const height = Math.max(RULER_HEIGHT + TRACK_HEIGHT,
    rows.length > 0 ? rows[rows.length - 1].y + rows[rows.length - 1].height : RULER_HEIGHT);
  const playheadX = (state.playheadSample / sampleRate) * pixelsPerSecond;
  const loopActive = state.project.loopEndSample > state.project.loopStartSample;
  const tempo = state.project.tempo > 0 ? state.project.tempo : 120;
  const snapSamples = snap === 'off'
    ? 0
    : snap === 'beat'
      ? (60 / tempo) * sampleRate
      : snap === 'bar'
        ? (240 / tempo) * sampleRate
        : Number(snap) * sampleRate;

  const snapSample = (sample: number) => snapSamples > 0
    ? Math.max(0, Math.round(sample / snapSamples) * snapSamples)
    : Math.max(0, Math.round(sample));

  useEffect(() => {
    setTempoDraft(String(Math.round(tempo)));
  }, [tempo]);

  const commitTempo = () => {
    const value = Number(tempoDraft);
    if (Number.isFinite(value)) {
      const next = Math.max(20, Math.min(300, Math.round(value)));
      setTempoDraft(String(next));
      onSetTempo(next);
    } else {
      setTempoDraft(String(Math.round(tempo)));
    }
  };

  const zoomBy = (factor: number) => {
    setZoom(current => Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, current * factor)));
  };

  const fitTimeline = () => {
    const viewport = scrollRef.current?.clientWidth ?? 900;
    if (scrollRef.current) scrollRef.current.scrollLeft = 0;
    setZoom(Math.max(MIN_ZOOM, Math.min(MAX_ZOOM,
      (viewport - 2) / Math.max(1, seconds * BASE_PIXELS_PER_SECOND))));
  };

  const endPan = () => {
    panRef.current = null;
    setPanning(false);
  };

  useEffect(() => {
    const element = scrollRef.current;
    if (!element) return;
    const handleWheel = (event: WheelEvent) => {
      if (event.shiftKey && !event.ctrlKey) {
        event.preventDefault();
        element.scrollLeft += event.deltaY;
      } else if (event.ctrlKey) {
        event.preventDefault();
        setZoom(current => Math.max(MIN_ZOOM, Math.min(MAX_ZOOM,
          current * (event.deltaY > 0 ? 0.8 : 1.25))));
      }
    };

    element.addEventListener('wheel', handleWheel, { passive: false });
    return () => element.removeEventListener('wheel', handleWheel);
  }, []);

  const targetFor = (lane: TimelineAutomationLaneModel) =>
    automationTargets.find(target => target.nodeId === lane.targetNodeId &&
      target.processorIndex === lane.processorIndex &&
      target.parameterIndex === lane.parameterIndex);

  const targetLabel = (lane: TimelineAutomationLaneModel) =>
    targetFor(lane)?.label ?? t('Unknown parameter');

  const automationValueY = (lane: TimelineAutomationLaneModel, row: TimelineRow, value: number) => {
    const target = targetFor(lane);
    const min = target?.min ?? 0;
    const max = target?.max ?? 1;
    const range = Math.max(0.000001, max - min);
    return row.y + row.height - 5 -
      ((Math.max(min, Math.min(max, value)) - min) / range) * (row.height - 14);
  };

  const pointsForLane = (trackId: string, lane: TimelineAutomationLaneModel) =>
    automationDrag && automationDrag.trackId === trackId && automationDrag.laneId === lane.id
      ? automationDrag.points
      : lane.points;

  const pointAt = (lane: TimelineAutomationLaneModel, row: TimelineRow, x: number, y: number) => {
    let best = -1;
    let bestDistance = 9;
    pointsForLane(row.track.id, lane).forEach((point, index) => {
      const pointX = (point.sample / sampleRate) * pixelsPerSecond;
      const pointY = automationValueY(lane, row, point.value);
      const distance = Math.hypot(pointX - x, pointY - y);
      if (distance < bestDistance) {
        best = index;
        bestDistance = distance;
      }
    });
    return best;
  };

  const valueAtY = (lane: TimelineAutomationLaneModel, row: TimelineRow, y: number) => {
    const target = targetFor(lane);
    const min = target?.min ?? 0;
    const max = target?.max ?? 1;
    const value = min + ((row.y + row.height - 5 - y) / Math.max(1, row.height - 14)) * (max - min);
    return Math.max(min, Math.min(max, value));
  };

  const updateAutomationDrag = (clientX: number, clientY: number) => {
    if (!automationDrag) return;
    const row = rows.find(item => item.lane?.id === automationDrag.laneId);
    if (!row || !row.lane) return;
    const deltaX = ((clientX - automationDrag.pointerStartX) / pixelsPerSecond) * sampleRate;
    const original = automationDrag.originalPoints[automationDrag.pointIndex];
    if (!original) return;
    const sample = snapSample(original.sample + deltaX);
    const value = valueAtY(row.lane, row, clientY);
    setAutomationDrag(current => {
      if (!current) return current;
      return {
        ...current,
        points: current.originalPoints.map((point, index) =>
          index === current.pointIndex ? { ...point, sample, value } : point),
      };
    });
  };

  const previewFor = (trackId: string, clip: TimelineClipModel) =>
    drag && drag.trackId === trackId && drag.clipId === clip.id && drag.targetTrackId === trackId
      ? drag.preview
      : clip;

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const dpr = window.devicePixelRatio || 1;
    const pixelWidth = Math.round(width * dpr);
    const pixelHeight = Math.round(height * dpr);
    if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
    if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.lineWidth = 1;
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = '#101720';
    ctx.fillRect(0, 0, width, height);
    ctx.fillStyle = '#141d27';
    ctx.fillRect(0, 0, width, RULER_HEIGHT);
    ctx.strokeStyle = '#314557';
    ctx.beginPath();
    ctx.moveTo(0, RULER_HEIGHT + 0.5);
    ctx.lineTo(width, RULER_HEIGHT + 0.5);
    ctx.stroke();

    const gridStep = Math.max(1, Math.ceil(50 / pixelsPerSecond));
    const labelStep = gridStep * 5;
    for (let second = 0; second <= seconds; second += gridStep) {
      const x = second * pixelsPerSecond;
      const major = second % labelStep === 0;
      ctx.strokeStyle = major ? '#385064' : '#202c38';
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, height);
      ctx.stroke();
      if (major) {
        ctx.fillStyle = '#71879a';
        ctx.font = '9px Consolas, monospace';
        ctx.fillText(`${second}s`, x + 3, 14);
      }
    }

    const beatSeconds = 60 / tempo;
    if (beatSeconds * pixelsPerSecond >= 8) {
      for (let beat = 0; beat * beatSeconds <= seconds; beat++) {
        const x = beat * beatSeconds * pixelsPerSecond;
        ctx.strokeStyle = beat % 4 === 0 ? '#2b4356' : '#1a2833';
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, height);
        ctx.stroke();
      }
    }

    if (loopActive) {
      const startX = (state.project.loopStartSample / sampleRate) * pixelsPerSecond;
      const endX = (state.project.loopEndSample / sampleRate) * pixelsPerSecond;
      ctx.fillStyle = 'rgba(59, 142, 178, 0.10)';
      ctx.fillRect(startX, 0, Math.max(1, endX - startX), height);
      ctx.strokeStyle = '#3b8eb2';
      ctx.beginPath();
      ctx.moveTo(startX, 0);
      ctx.lineTo(startX, height);
      ctx.moveTo(endX, 0);
      ctx.lineTo(endX, height);
      ctx.stroke();
    }

    for (const row of rows) {
      if (row.kind === 'track') {
        const index = row.index;
        ctx.fillStyle = index % 2 === 0 ? '#151f2a' : '#121b24';
        ctx.fillRect(0, row.y, width, row.height);
        ctx.fillStyle = row.track.muted ? '#6f7c86' : '#9fb5c6';
        ctx.font = '10px Consolas, monospace';
        ctx.fillText(row.track.name, 6, row.y + 14);
        if (row.track.solo) {
          ctx.fillStyle = '#ffd166';
          ctx.fillText('SOLO', 6 + ctx.measureText(row.track.name).width + 10, row.y + 14);
        }

        for (const clip of row.track.clips) {
          const view = previewFor(row.track.id, clip);
          const x = (view.startSample / sampleRate) * pixelsPerSecond;
          const w = Math.max(2, (view.lengthSamples / sampleRate) * pixelsPerSecond);
          const clipY = row.y + 20;
          const clipH = row.height - 28;
          ctx.fillStyle = '#2d7193';
          ctx.fillRect(x, clipY, w, clipH);
          const isSelected = selectedClip?.trackId === row.track.id && selectedClip.clipId === clip.id;
          ctx.strokeStyle = isSelected ? '#ffd166' : '#64b5d8';
          ctx.lineWidth = isSelected ? 2 : 1;
          ctx.strokeRect(x + 0.5, clipY + 0.5, Math.max(1, w - 1), clipH - 1);
          ctx.lineWidth = 1;

          if (clip.peaks?.length) {
            const center = row.y + row.height / 2 + 4;
            const half = (row.height - 30) / 2;
            ctx.strokeStyle = '#9bdcff';
            ctx.beginPath();
            for (let p = 0; p < clip.peaks.length; p++) {
              const px = x + (p / clip.peaks.length) * w;
              const peak = Math.max(1, clip.peaks[p] * half);
              ctx.moveTo(px, center - peak);
              ctx.lineTo(px, center + peak);
            }
            ctx.stroke();
          }

          const fadeInWidth = (view.fadeInSamples / sampleRate) * pixelsPerSecond;
          const fadeOutWidth = (view.fadeOutSamples / sampleRate) * pixelsPerSecond;
          if (fadeInWidth > 0) {
            ctx.strokeStyle = '#ffd166';
            ctx.beginPath();
            ctx.moveTo(x, clipY + clipH);
            ctx.lineTo(x + Math.min(w, fadeInWidth), clipY);
            ctx.stroke();
          }
          if (fadeOutWidth > 0) {
            ctx.strokeStyle = '#ffd166';
            ctx.beginPath();
            ctx.moveTo(x + w - Math.min(w, fadeOutWidth), clipY);
            ctx.lineTo(x + w, clipY + clipH);
            ctx.stroke();
          }

          ctx.fillStyle = '#ffd166';
          ctx.beginPath();
          ctx.arc(x + 7, clipY + 6, 3.5, 0, Math.PI * 2);
          ctx.fill();
          ctx.beginPath();
          ctx.arc(x + w - 7, clipY + 6, 3.5, 0, Math.PI * 2);
          ctx.fill();

          ctx.fillStyle = '#d7f0ff';
          ctx.font = '9px Consolas, monospace';
          const label = clip.name || t('Audio clip');
          ctx.fillText(label, x + 4, row.y + 34);
        }

        if (drag?.kind === 'move' && drag.targetTrackId === row.track.id &&
            drag.targetTrackId !== drag.trackId) {
          const sourceName = tracks
            .find(track => track.id === drag.trackId)
            ?.clips.find(clip => clip.id === drag.clipId)?.name;
          const x = (drag.preview.startSample / sampleRate) * pixelsPerSecond;
          const w = Math.max(2, (drag.preview.lengthSamples / sampleRate) * pixelsPerSecond);
          ctx.save();
          ctx.globalAlpha = 0.45;
          ctx.fillStyle = '#2d7193';
          ctx.fillRect(x, row.y + 20, w, row.height - 28);
          ctx.globalAlpha = 0.95;
          ctx.setLineDash([4, 3]);
          ctx.strokeStyle = '#ffd166';
          ctx.strokeRect(x + 0.5, row.y + 20.5, Math.max(1, w - 1), row.height - 29);
          ctx.setLineDash([]);
          if (sourceName) {
            ctx.fillStyle = '#ffe6a3';
            ctx.font = '9px Consolas, monospace';
            ctx.fillText(sourceName, x + 4, row.y + 34);
          }
          ctx.restore();
        }
      } else if (row.lane) {
        const lane = row.lane;
        const target = targetFor(lane);
        const min = target?.min ?? 0;
        const max = target?.max ?? 1;
        ctx.fillStyle = '#0d141b';
        ctx.fillRect(0, row.y, width, row.height);
        ctx.strokeStyle = '#27394a';
        ctx.beginPath();
        ctx.moveTo(0, row.y);
        ctx.lineTo(width, row.y);
        ctx.stroke();
        ctx.fillStyle = '#8ea8bb';
        ctx.font = '9px Consolas, monospace';
        ctx.fillText(`${targetLabel(lane)}  ${min.toFixed(2)}..${max.toFixed(2)}`, 6, row.y + 11);

        const valueY = (value: number) => automationValueY(lane, row, value);
        const points = [...pointsForLane(row.track.id, lane)].sort((a, b) => a.sample - b.sample);
        if (points.length > 0) {
          ctx.strokeStyle = '#6bd6a8';
          ctx.beginPath();
          ctx.moveTo((points[0].sample / sampleRate) * pixelsPerSecond, valueY(points[0].value));
          for (let i = 1; i < points.length; i++) {
            const previous = points[i - 1];
            const next = points[i];
            const previousX = (previous.sample / sampleRate) * pixelsPerSecond;
            const nextX = (next.sample / sampleRate) * pixelsPerSecond;
            const previousY = valueY(previous.value);
            const nextY = valueY(next.value);
            if (previous.curve === 1) {
              ctx.lineTo(nextX, previousY);
              ctx.lineTo(nextX, nextY);
            } else if (previous.curve === 2) {
              for (let step = 1; step <= 16; step++) {
                const t = step / 16;
                ctx.lineTo(previousX + (nextX - previousX) * t,
                  previousY + (nextY - previousY) * t * t);
              }
            } else {
              ctx.lineTo(nextX, nextY);
            }
          }
          ctx.stroke();
          for (let index = 0; index < points.length; index++) {
            const point = points[index];
            const pointX = (point.sample / sampleRate) * pixelsPerSecond;
            const active = automationDrag?.laneId === lane.id &&
              automationDrag.points[automationDrag.pointIndex] === point;
            ctx.fillStyle = active ? '#ffd166' : '#6bd6a8';
            ctx.beginPath();
            ctx.arc(pointX, valueY(point.value), active ? 4 : 3, 0, Math.PI * 2);
            ctx.fill();
          }
        }
      }
    }

    ctx.fillStyle = '#ff7b72';
    ctx.beginPath();
    ctx.moveTo(playheadX - 5, 0);
    ctx.lineTo(playheadX + 5, 0);
    ctx.lineTo(playheadX, 8);
    ctx.fill();
    ctx.strokeStyle = '#ff7b72';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(playheadX, 0);
    ctx.lineTo(playheadX, height);
    ctx.stroke();
  }, [automationDrag, automationTargets, drag, height, loopActive, pixelsPerSecond, rows, sampleRate, seconds, selectedClip, state.playheadSample, state.project.loopEndSample, state.project.loopStartSample, t, tempo, width]);

  const locate = (clientX: number, clientY: number) => {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const rect = canvas.getBoundingClientRect();
    const x = clientX - rect.left;
    const y = clientY - rect.top;
    const row = rows.find(item => y >= item.y && y < item.y + item.height);
    if (!row) return null;
    const sample = Math.max(0, (x / pixelsPerSecond) * sampleRate);
    return { canvas, row, x, y, sample };
  };

  const sampleAtX = (clientX: number) => {
    const canvas = canvasRef.current;
    if (!canvas) return 0;
    const rect = canvas.getBoundingClientRect();
    return Math.max(0, ((clientX - rect.left) / pixelsPerSecond) * sampleRate);
  };

  const clipHit = (row: TimelineRow, x: number, y: number, sample: number) => {
    if (row.kind !== 'track') return null;
    if (y < row.y + 20 || y > row.y + row.height - 8) return null;
    const clip = [...row.track.clips].reverse().find(item => {
      const view = previewFor(row.track.id, item);
      return sample >= view.startSample && sample <= view.startSample + view.lengthSamples;
    });
    if (!clip) return null;
    const view = previewFor(row.track.id, clip);
    const clipX = (view.startSample / sampleRate) * pixelsPerSecond;
    const clipW = Math.max(2, (view.lengthSamples / sampleRate) * pixelsPerSecond);
    const inFadeStrip = y >= row.y + 20 && y <= row.y + 34;
    if (inFadeStrip && Math.abs(x - (clipX + 7)) <= 8 && Math.abs(sample - view.startSample) / sampleRate * pixelsPerSecond <= 20) {
      return { clip, view, kind: 'fade-in' as DragKind };
    }
    if (inFadeStrip && Math.abs(x - (clipX + clipW - 7)) <= 8 && Math.abs((view.startSample + view.lengthSamples) - sample) / sampleRate * pixelsPerSecond <= 20) {
      return { clip, view, kind: 'fade-out' as DragKind };
    }
    if (x <= clipX + 5) return { clip, view, kind: 'resize-left' as DragKind };
    if (x >= clipX + clipW - 5) return { clip, view, kind: 'resize-right' as DragKind };
    return { clip, view, kind: 'move' as DragKind };
  };

  const updateDrag = (clientX: number, clientY: number) => {
    if (!drag) return;
    const deltaSamples = ((clientX - drag.pointerStartX) / pixelsPerSecond) * sampleRate;
    const original = drag.original;
    let preview = { ...original };
    let targetTrackId = drag.targetTrackId;
    if (drag.kind === 'move') {
      const target = locate(clientX, clientY)?.row;
      if (target?.kind === 'track') {
        targetTrackId = target.track.id;
      }
      preview.startSample = Math.max(0, snapSample(original.startSample + deltaSamples));
    } else if (drag.kind === 'resize-left') {
      const nextStart = Math.max(0, Math.min(
        original.startSample + original.lengthSamples - 1,
        snapSample(original.startSample + deltaSamples),
      ));
      const trim = nextStart - original.startSample;
      preview.startSample = nextStart;
      preview.sourceOffsetSamples = Math.max(0, original.sourceOffsetSamples + trim);
      preview.lengthSamples = Math.max(1, original.lengthSamples - trim);
      preview.fadeInSamples = Math.min(preview.fadeInSamples, preview.lengthSamples);
      preview.fadeOutSamples = Math.min(preview.fadeOutSamples, preview.lengthSamples);
    } else if (drag.kind === 'resize-right') {
      const nextEnd = snapSample(original.startSample + original.lengthSamples + deltaSamples);
      preview.lengthSamples = Math.max(1, nextEnd - original.startSample);
      preview.fadeInSamples = Math.min(preview.fadeInSamples, preview.lengthSamples);
      preview.fadeOutSamples = Math.min(preview.fadeOutSamples, preview.lengthSamples);
    } else if (drag.kind === 'stretch-right') {
      const nextEnd = snapSample(original.startSample + original.lengthSamples + deltaSamples);
      const length = Math.max(
        Math.ceil(original.lengthSamples * 0.5),
        Math.min(Math.floor(original.lengthSamples * 2), nextEnd - original.startSample),
      );
      const factor = length / Math.max(1, original.lengthSamples);
      preview.lengthSamples = length;
      preview.fadeInSamples = Math.min(length, Math.round(original.fadeInSamples * factor));
      preview.fadeOutSamples = Math.min(length, Math.round(original.fadeOutSamples * factor));
    } else if (drag.kind === 'fade-in') {
      preview.fadeInSamples = Math.max(0, Math.min(preview.lengthSamples,
        Math.round(original.fadeInSamples + deltaSamples)));
    } else if (drag.kind === 'fade-out') {
      preview.fadeOutSamples = Math.max(0, Math.min(preview.lengthSamples,
        Math.round(original.fadeOutSamples - deltaSamples)));
    }

    setDrag(current => current ? { ...current, preview, targetTrackId } : current);
  };

  const finishAutomationDrag = () => {
    if (!automationDrag) return;
    const changed = automationDrag.points.some((point, index) => {
      const original = automationDrag.originalPoints[index];
      return !original || original.sample !== point.sample || original.value !== point.value;
    });
    if (changed) {
      onSetAutomationPoints(automationDrag.trackId, automationDrag.laneId, automationDrag.points);
    }
    setAutomationDrag(null);
  };

  const cancelAutomationDrag = () => setAutomationDrag(null);
  const cancelDrag = () => setDrag(null);

  const finishDrag = () => {
    if (!drag) return;
    const targetTrackId = drag.targetTrackId;
    const changedTracks = drag.kind === 'move' && targetTrackId !== drag.trackId;
    const changed = changedTracks ||
      drag.preview.startSample !== drag.original.startSample ||
      drag.preview.lengthSamples !== drag.original.lengthSamples ||
      drag.preview.sourceOffsetSamples !== drag.original.sourceOffsetSamples ||
      drag.preview.fadeInSamples !== drag.original.fadeInSamples ||
      drag.preview.fadeOutSamples !== drag.original.fadeOutSamples;
    if (changed) {
      if (drag.kind === 'move') {
        if (changedTracks) {
          onMoveClipToTrack(drag.trackId, targetTrackId, drag.clipId, drag.preview.startSample);
          setSelectedClip({ trackId: targetTrackId, clipId: drag.clipId });
        } else {
          onMoveClip(drag.trackId, drag.clipId, drag.preview.startSample);
        }
      } else if (drag.kind === 'stretch-right') {
        onStretchClip(drag.trackId, drag.clipId, drag.preview.lengthSamples);
      } else {
        onUpdateClip(drag.trackId, drag.clipId, drag.preview);
      }
    }
    setDrag(null);
  };

  return (
    <section className="timeline-dock" aria-label={t('Timeline')}>
      <header className="timeline-header">
        <strong>{t('Timeline')}</strong>
        <span className="timeline-position">
          {(state.playheadSample / sampleRate).toFixed(2)}s
        </span>
        <div className="timeline-actions">
          <div className="timeline-zoom" role="group" aria-label={t('Timeline zoom')}>
            <button className="icon-button compact" onClick={() => zoomBy(0.5)} title={t('Zoom out')} aria-label={t('Zoom out')}>−</button>
            <span>{Math.round((pixelsPerSecond / BASE_PIXELS_PER_SECOND) * 100)}%</span>
            <button className="icon-button compact" onClick={() => zoomBy(2)} title={t('Zoom in')} aria-label={t('Zoom in')}>+</button>
            <button className="tool-button" onClick={fitTimeline} title={t('Fit timeline')}>{t('Fit')}</button>
          </div>
          <label className="timeline-tempo">
            <span>{t('Tempo')}</span>
            <input
              type="number"
              min={20}
              max={300}
              step={1}
              value={tempoDraft}
              onChange={event => setTempoDraft(event.target.value)}
              onBlur={commitTempo}
              onKeyDown={event => {
                if (event.key === 'Enter') {
                  commitTempo();
                  event.currentTarget.blur();
                }
              }}
            />
            <span>BPM</span>
          </label>
          {selectedClipModel && (
            <label className="timeline-clip-gain">
              <span>{t('Clip gain')}</span>
              <span className="volume-slider">
                <input
                  type="range"
                  min={0}
                  max={1.5}
                  step={0.01}
                  value={clipGain}
                  aria-label={t('Clip gain')}
                  aria-valuetext={formatGainDb(clipGain)}
                  onChange={event => queueClipGain(Number(event.target.value))}
                  onDoubleClick={() => queueClipGain(1, true)}
                  onKeyDown={event => {
                    const next = nextVolumeValue(event, clipGain);
                    if (next !== null) {
                      event.preventDefault();
                      queueClipGain(next, true);
                    }
                  }}
                />
                <small className="volume-db">{formatGainDb(clipGain)}</small>
              </span>
            </label>
          )}
          <button className="tool-button" onClick={onUndo} disabled={!state.canUndo}>{t('Undo')}</button>
          <button className="tool-button" onClick={onRedo} disabled={!state.canRedo}>{t('Redo')}</button>
          <button className="tool-button" onClick={onPlay} disabled={state.playing} title={withShortcut(t('Play'), 'timelinePlay')}>{t('Play')}</button>
          <button className="tool-button" onClick={onStop} disabled={!state.playing} title={withShortcut(t('Stop'), 'timelinePlay')}>{t('Stop')}</button>
          <button className={`tool-button ${state.recording ? 'danger' : ''}`} aria-pressed={state.recording === true} onClick={onToggleRecord}>
            {state.recording ? t('Stop record') : t('Record')}
          </button>
          <button className={`tool-button ${loopActive ? 'active' : ''}`} onClick={() => loopActive ? onSetLoop(null, null) : onSetLoop(0, maxEnd)}>{t('Loop')}</button>
          <select className="timeline-snap" value={snap} onChange={event => setSnap(event.target.value)} aria-label={t('Snap')}>
            <option value="off">{t('Snap off')}</option>
            <option value="0.1">{t('Snap 0.1s')}</option>
            <option value="0.25">{t('Snap 0.25s')}</option>
            <option value="1">{t('Snap 1s')}</option>
            <option value="beat">{t('Snap beat')}</option>
            <option value="bar">{t('Snap bar')}</option>
          </select>
          <button className="tool-button" onClick={onAddTrack}>{t('Add track')}</button>
          <button className="tool-button" onClick={onImport}>{t('Import clip')}</button>
          <button className="tool-button" onClick={onBounce}>{t('Bounce mix')}</button>
          <button className="tool-button" onClick={onFreeze}>{t('Freeze')}</button>
          <button className="icon-button compact" onClick={onClose} aria-label={t('Close timeline')}>×</button>
        </div>
      </header>
      {tracks.length > 0 && (
        <div className="timeline-track-strip">
          {tracks.map(track => {
            const hasNode = Boolean(track.sourceNodeId && nodeIds.has(track.sourceNodeId));
            return (
            <div className="timeline-track-control" key={track.id}>
              <div className="timeline-track-heading">
                <strong>{track.name}</strong>
                <span className="timeline-track-actions">
                  <button
                    className="icon-button compact"
                    title={hasNode ? t('Show track node') : t('Restore track node')}
                    aria-label={hasNode ? t('Show track node') : t('Restore track node')}
                    onClick={() => onShowTrackNode(track.id, track.sourceNodeId)}
                  >{hasNode ? '↗' : '+'}</button>
                  <button
                    className="icon-button compact danger-text"
                    title={t('Remove track')}
                    aria-label={t('Remove track')}
                    onClick={() => setPendingRemoval({ kind: 'track', trackId: track.id })}
                  >×</button>
                </span>
              </div>
              <div className="timeline-track-mix">
                <button
                  className={`icon-button compact ${track.muted ? 'danger-text' : ''}`}
                  title={track.muted ? t('Unmute') : t('Mute')}
                  aria-label={track.muted ? t('Unmute') : t('Mute')}
                  aria-pressed={track.muted}
                  onClick={() => onTrackUpdate(track.id, track.gain, !track.muted, track.solo)}
                >M</button>
                <button
                  className={`icon-button compact ${track.solo ? 'active' : ''}`}
                  title={track.solo ? t('Unsolo') : t('Solo')}
                  aria-label={track.solo ? t('Unsolo') : t('Solo')}
                  aria-pressed={track.solo}
                  onClick={() => onTrackUpdate(track.id, track.gain, track.muted, !track.solo)}
                >S</button>
                <TrackGainSlider
                  track={track}
                  onChange={gain => onTrackUpdate(track.id, gain, track.muted, track.solo)}
                />
                <select
                  className="timeline-automation-target"
                  value=""
                  aria-label={`${track.name} ${t('Automation target')}`}
                  onChange={event => {
                    if (event.target.value === '') return;
                    const target = automationTargets[Number(event.target.value)];
                    if (target) onAddAutomation(track.id, target);
                  }}
                >
                  <option value="">+ A</option>
                  {automationTargets.map((target, index) => (
                    <option value={index} key={`${target.nodeId}-${target.processorIndex}-${target.parameterIndex}`}>
                      {target.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          );
          })}
        </div>
      )}
      <div
        className={`timeline-scroll ${panning ? 'panning' : ''} ${seeking ? 'seeking' : ''} ${dropActive ? 'drop-active' : ''}`}
        ref={scrollRef}
        onDragOver={event => {
          const types = Array.from(event.dataTransfer.types);
          if (types.includes('Files') || types.includes('application/ruse-item') ||
              types.includes('application/ruse-recording')) {
            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
            setDropActive(true);
          }
        }}
        onDragLeave={event => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropActive(false);
        }}
        onDrop={event => {
          event.preventDefault();
          setDropActive(false);
          const hit = locate(event.clientX, event.clientY);
          const trackId = hit?.row.track.id ?? tracks[0]?.id ?? '';
          const sample = snapSample(sampleAtX(event.clientX));
          const files = Array.from(event.dataTransfer.files ?? []);
          if (files.length > 0) {
            window.chrome?.webview?.postMessageWithAdditionalObjects?.(
              { type: 'timeline-file-drop', payload: { trackId, startSample: sample } },
              files,
            );
            return;
          }

          const raw = event.dataTransfer.getData('application/ruse-item');
          if (raw) {
            try {
              const item = JSON.parse(raw) as { kind?: string; filePath?: string };
              if (item.kind === 'sample' && item.filePath) {
                onAddClipPath(trackId, item.filePath, sample);
              }
            } catch {
            }

            return;
          }

          const path = event.dataTransfer.getData('application/ruse-recording');
          if (path) onAddClipPath(trackId, path, sample);
        }}
      >
        {tracks.length === 0 ? (
          <div className="timeline-empty">
            <span>{t('No timeline tracks yet.')}</span>
            <span className="timeline-empty-hint">{t('Add a track, then drop samples or recordings onto the timeline.')}</span>
            <button className="primary-button" onClick={onAddTrack}>{t('Add track')}</button>
          </div>
        ) : (
          <canvas
            ref={canvasRef}
            tabIndex={0}
            role="application"
            aria-label={t('Timeline')}
            aria-keyshortcuts="Delete Backspace ArrowLeft ArrowRight Home End"
            title={t('Shift+drag a clip edge to time-stretch')}
            onKeyDown={event => {
              const selectedTrack = selectedClip
                ? tracks.find(track => track.id === selectedClip.trackId)
                : undefined;
              const selected = selectedClip
                ? selectedTrack?.clips.find(clip => clip.id === selectedClip.clipId)
                : undefined;
              if ((event.key === 'Delete' || event.key === 'Backspace') && selectedClip) {
                event.preventDefault();
                onRemoveClip(selectedClip.trackId, selectedClip.clipId);
                setSelectedClip(null);
              } else if (event.key === 'Escape') {
                setSelectedClip(null);
              } else if (event.shiftKey && selectedClip && selected && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
                event.preventDefault();
                const delta = snapSamples > 0 ? snapSamples : 1;
                onMoveClip(selectedClip.trackId, selectedClip.clipId,
                  Math.max(0, selected.startSample + (event.key === 'ArrowLeft' ? -delta : delta)));
              } else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
                event.preventDefault();
                const delta = sampleRate * 0.1 * (event.key === 'ArrowLeft' ? -1 : 1);
                onSeek(snapSample(Math.max(0, state.playheadSample + delta)));
              } else if (event.key === 'Home') {
                event.preventDefault();
                onSeek(0);
              } else if (event.key === 'End') {
                event.preventDefault();
                onSeek(snapSample(maxEnd));
              }
            }}
            onPointerDown={event => {
              if (event.button === 1 || (event.button === 0 && event.altKey)) {
                panRef.current = {
                  clientX: event.clientX,
                  scrollLeft: scrollRef.current?.scrollLeft ?? 0,
                };
                setPanning(true);
                event.preventDefault();
                event.currentTarget.setPointerCapture(event.pointerId);
                return;
              }

              if (event.button !== 0) return;
              const canvas = canvasRef.current;
              if (!canvas) return;
              const canvasRect = canvas.getBoundingClientRect();
              const sample = sampleAtX(event.clientX);
              if (event.clientY - canvasRect.top <= RULER_HEIGHT) {
                setSeeking(true);
                onSeek(snapSample(sample));
                event.currentTarget.setPointerCapture(event.pointerId);
                return;
              }

              const hit = locate(event.clientX, event.clientY);
              if (!hit) {
                onSeek(snapSample(sample));
                return;
              }
              if (hit.row.kind === 'automation' && hit.row.lane) {
                const lane = hit.row.lane;
                const hitIndex = pointAt(lane, hit.row, hit.x, hit.y);
                if (hitIndex >= 0) {
                  const point = lane.points[hitIndex];
                  const originalPoints = [...lane.points].sort((a, b) => a.sample - b.sample);
                  setAutomationDrag({
                    trackId: hit.row.track.id,
                    laneId: lane.id,
                    pointIndex: originalPoints.findIndex(item => item === point),
                    pointerStartX: event.clientX,
                    originalPoints,
                    points: originalPoints.map(item => ({ ...item })),
                  });
                  event.currentTarget.setPointerCapture(event.pointerId);
                } else {
                  onSeek(snapSample(hit.sample));
                }
                return;
              }

              const clip = clipHit(hit.row, hit.x, hit.y, hit.sample);
              if (!clip) {
                setSelectedClip(null);
                onSeek(snapSample(hit.sample));
                return;
              }

              setSelectedClip({ trackId: hit.row.track.id, clipId: clip.clip.id });
              event.currentTarget.focus();

              const original: ClipPreview = {
                startSample: clip.view.startSample,
                lengthSamples: clip.view.lengthSamples,
                sourceOffsetSamples: clip.view.sourceOffsetSamples,
                fadeInSamples: clip.view.fadeInSamples,
                fadeOutSamples: clip.view.fadeOutSamples,
              };
              setDrag({
                kind: clip.kind === 'resize-right' && event.shiftKey ? 'stretch-right' : clip.kind,
                trackId: hit.row.track.id,
                clipId: clip.clip.id,
                targetTrackId: hit.row.track.id,
                pointerStartX: event.clientX,
                original,
                preview: { ...original },
              });
              event.currentTarget.setPointerCapture(event.pointerId);
            }}
            onPointerMove={event => {
              const pan = panRef.current;
              if (pan && scrollRef.current) {
                scrollRef.current.scrollLeft = pan.scrollLeft - (event.clientX - pan.clientX);
                return;
              }

              if (seeking) {
                onSeek(snapSample(sampleAtX(event.clientX)));
                return;
              }

              if (automationDrag) updateAutomationDrag(event.clientX, event.clientY);
              else if (drag) updateDrag(event.clientX, event.clientY);
            }}
            onPointerUp={() => { setSeeking(false); endPan(); finishAutomationDrag(); finishDrag(); }}
            onPointerCancel={() => { setSeeking(false); endPan(); cancelAutomationDrag(); cancelDrag(); }}
            onDoubleClick={event => {
              const hit = locate(event.clientX, event.clientY);
              if (!hit || hit.row.kind !== 'automation' || !hit.row.lane) return;
              const lane = hit.row.lane;
              const pointIndex = pointAt(lane, hit.row, hit.x, hit.y);
              if (pointIndex >= 0) {
                const points = lane.points.map((point, index) => index === pointIndex
                  ? { ...point, curve: (point.curve + 1) % 3 }
                  : point);
                onSetAutomationPoints(hit.row.track.id, lane.id, points);
                return;
              }
              const target = targetFor(lane);
              const min = target?.min ?? 0;
              const max = target?.max ?? 1;
              const value = valueAtY(lane, hit.row, hit.y);
              const sample = snapSample(hit.sample);
              const points = [
                ...lane.points.filter(point => point.sample !== sample),
                {
                  sample,
                  value: Math.max(min, Math.min(max, value)),
                  curve: 0,
                },
              ].sort((a, b) => a.sample - b.sample);
              onSetAutomationPoints(hit.row.track.id, lane.id, points);
            }}
            onContextMenu={event => {
              const hit = locate(event.clientX, event.clientY);
              if (!hit) return;
              event.preventDefault();
              if (hit.row.kind === 'automation' && hit.row.lane) {
                const lane = hit.row.lane;
                let closest: { sample: number; value: number; curve: number } | null = null;
                let closestDistance = 9;
                for (const point of lane.points) {
                  const pointX = (point.sample / sampleRate) * pixelsPerSecond;
                  const pointY = automationValueY(lane, hit.row, point.value);
                  const distance = Math.hypot(pointX - hit.x, pointY - hit.y);
                  if (distance < closestDistance) {
                    closest = point;
                    closestDistance = distance;
                  }
                }
                if (closest) {
                  const points = lane.points.filter(point => point.sample !== closest!.sample);
                  if (points.length > 0) onSetAutomationPoints(hit.row.track.id, lane.id, points);
                  else onRemoveAutomation(hit.row.track.id, lane.id);
                } else {
                  setPendingRemoval({ kind: 'automation', trackId: hit.row.track.id, laneId: lane.id });
                }
                return;
              }
              const clip = clipHit(hit.row, hit.x, hit.y, hit.sample);
              if (clip) {
                setPendingRemoval({ kind: 'clip', trackId: hit.row.track.id, clipId: clip.clip.id });
              }
            }}
          />
        )}
      </div>
      {pendingRemoval && (
        <ConfirmDialog
          title={pendingRemoval.kind === 'clip'
            ? t('Remove this clip?')
            : pendingRemoval.kind === 'track'
              ? t('Remove track')
              : t('Remove this automation lane?')}
          confirmLabel={t('Remove')}
          onConfirm={() => {
            if (pendingRemoval.kind === 'clip') onRemoveClip(pendingRemoval.trackId, pendingRemoval.clipId);
            else if (pendingRemoval.kind === 'track') onRemoveTrack(pendingRemoval.trackId);
            else onRemoveAutomation(pendingRemoval.trackId, pendingRemoval.laneId);
            setPendingRemoval(null);
          }}
          onClose={() => setPendingRemoval(null)}
        />
      )}
    </section>
  );
}

