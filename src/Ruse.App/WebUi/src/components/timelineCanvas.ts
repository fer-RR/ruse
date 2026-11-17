import type {
  TimelineAutomationLaneModel,
  TimelineAutomationTarget,
  TimelineClipModel,
  TimelineMarkerModel,
  TimelineTrackModel,
} from '../timeline';
import type { TimelinePalette } from '../timelinePalette';

export const RULER_HEIGHT = 22;

export type DragKind = 'move' | 'resize-left' | 'resize-right' | 'stretch-right' | 'fade-in' | 'fade-out';

export interface ClipPreview {
  startSample: number;
  lengthSamples: number;
  sourceOffsetSamples: number;
  fadeInSamples: number;
  fadeOutSamples: number;
}

export interface TimelineDragState {
  kind: DragKind;
  trackId: string;
  clipId: string;
  targetTrackId: string;
  pointerStartX: number;
  original: ClipPreview;
  preview: ClipPreview;
}

export interface TimelineAutomationDrag {
  trackId: string;
  laneId: string;
  pointIndex: number;
  pointerStartX: number;
  originalPoints: { sample: number; value: number; curve: number }[];
  points: { sample: number; value: number; curve: number }[];
}

export interface TimelineRow {
  kind: 'track' | 'automation';
  index: number;
  track: TimelineTrackModel;
  lane?: TimelineAutomationLaneModel;
  y: number;
  height: number;
}

export interface TimelineCanvasParams {
  width: number;
  height: number;
  palette: TimelinePalette;
  seconds: number;
  beatSeconds: number;
  beatsPerBar: number;
  pixelsPerSecond: number;
  sampleRate: number;
  displayLoopActive: boolean;
  displayLoopStart: number;
  displayLoopEnd: number;
  rows: TimelineRow[];
  tracks: TimelineTrackModel[];
  selectedClipKeys: string[];
  selectedClipModel: { track: TimelineTrackModel; clip: TimelineClipModel } | null;
  drag: TimelineDragState | null;
  automationDrag: TimelineAutomationDrag | null;
  automationTargets: TimelineAutomationTarget[];
  markers: TimelineMarkerModel[];
  playheadX: number;
  audioClipLabel: string;
}

const clipKey = (trackId: string, clipId: string) => `${trackId}:${clipId}`;

export function drawTimeline(canvas: HTMLCanvasElement, params: TimelineCanvasParams): void {
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  const {
    width,
    height,
    palette,
    seconds,
    beatSeconds,
    beatsPerBar,
    pixelsPerSecond,
    sampleRate,
    displayLoopActive,
    displayLoopStart,
    displayLoopEnd,
    rows,
    tracks,
    selectedClipKeys,
    selectedClipModel,
    drag,
    automationDrag,
    automationTargets,
    markers,
    playheadX,
    audioClipLabel,
  } = params;

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
  ctx.fillStyle = palette.background;
  ctx.fillRect(0, 0, width, height);
  ctx.fillStyle = palette.ruler;
  ctx.fillRect(0, 0, width, RULER_HEIGHT);
  ctx.strokeStyle = palette.rulerLine;
  ctx.beginPath();
  ctx.moveTo(0, RULER_HEIGHT + 0.5);
  ctx.lineTo(width, RULER_HEIGHT + 0.5);
  ctx.stroke();

  const barSeconds = beatSeconds * beatsPerBar;
  const barCount = Math.ceil(seconds / barSeconds) + 1;
  for (let bar = 0; bar < barCount; bar++) {
    const x = bar * barSeconds * pixelsPerSecond;
    ctx.strokeStyle = palette.gridMajor;
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
    ctx.fillStyle = palette.rulerText;
    ctx.font = '9px Consolas, monospace';
    ctx.fillText(`${bar + 1}`, x + 3, 13);
  }

  const beatPixels = beatSeconds * pixelsPerSecond;
  if (beatPixels >= 8) {
    for (let beat = 1; beat * beatSeconds <= seconds; beat++) {
      if (beat % beatsPerBar === 0) continue;
      const x = beat * beatSeconds * pixelsPerSecond;
      ctx.strokeStyle = palette.beatMinor;
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, height);
      ctx.stroke();
    }
  }
  if (displayLoopActive) {
    const startX = (displayLoopStart / sampleRate) * pixelsPerSecond;
    const endX = (displayLoopEnd / sampleRate) * pixelsPerSecond;
    ctx.fillStyle = palette.loopFill;
    ctx.fillRect(startX, 0, Math.max(1, endX - startX), height);
    ctx.strokeStyle = palette.loopStroke;
    ctx.beginPath();
    ctx.moveTo(startX, 0);
    ctx.lineTo(startX, height);
    ctx.moveTo(endX, 0);
    ctx.lineTo(endX, height);
    ctx.stroke();
  }

  const previewFor = (trackId: string, clip: TimelineClipModel): ClipPreview =>
    drag && drag.trackId === trackId && drag.clipId === clip.id && drag.targetTrackId === trackId
      ? drag.preview
      : clip;

  const targetByKey = new Map<string, TimelineAutomationTarget>();
  for (const target of automationTargets) {
    targetByKey.set(`${target.nodeId}:${target.processorIndex}:${target.parameterIndex}`, target);
  }

  const targetFor = (lane: TimelineAutomationLaneModel) =>
    targetByKey.get(`${lane.targetNodeId}:${lane.processorIndex}:${lane.parameterIndex}`);

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

  for (const row of rows) {
    if (row.kind === 'track') {
      const index = row.index;
      ctx.fillStyle = index % 2 === 0 ? palette.trackA : palette.trackB;
      ctx.fillRect(0, row.y, width, row.height);

      for (const clip of row.track.clips) {
        const view = previewFor(row.track.id, clip);
        const x = (view.startSample / sampleRate) * pixelsPerSecond;
        const w = Math.max(2, (view.lengthSamples / sampleRate) * pixelsPerSecond);
        const clipY = row.y + 20;
        const clipH = row.height - 28;
        const generatorClip = Boolean(row.track.generatorNodeId);
        ctx.fillStyle = generatorClip ? palette.generatorClip : palette.clip;
        ctx.fillRect(x, clipY, w, clipH);
        const isSelected = selectedClipKeys.includes(clipKey(row.track.id, clip.id));
        ctx.strokeStyle = isSelected ? palette.fade : generatorClip ? palette.generatorStroke : palette.clipStroke;
        ctx.lineWidth = isSelected ? 2 : 1;
        ctx.strokeRect(x + 0.5, clipY + 0.5, Math.max(1, w - 1), clipH - 1);
        ctx.lineWidth = 1;

        if (generatorClip) {
          ctx.save();
          ctx.beginPath();
          ctx.rect(x, clipY, w, clipH);
          ctx.clip();
          ctx.strokeStyle = palette.generatorHatch;
          for (let line = x - clipH; line < x + w; line += 14) {
            ctx.beginPath();
            ctx.moveTo(line, clipY + clipH);
            ctx.lineTo(line + clipH, clipY);
            ctx.stroke();
          }
          ctx.restore();
        }

        if (clip.peaks?.length) {
          const center = row.y + row.height / 2 + 4;
          const half = (row.height - 30) / 2;
          ctx.strokeStyle = palette.waveform;
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
          ctx.strokeStyle = palette.fade;
          ctx.beginPath();
          ctx.moveTo(x, clipY + clipH);
          ctx.lineTo(x + Math.min(w, fadeInWidth), clipY);
          ctx.stroke();
        }
        if (fadeOutWidth > 0) {
          ctx.strokeStyle = palette.fade;
          ctx.beginPath();
          ctx.moveTo(x + w - Math.min(w, fadeOutWidth), clipY);
          ctx.lineTo(x + w, clipY + clipH);
          ctx.stroke();
        }

        ctx.fillStyle = palette.fade;
        ctx.beginPath();
        ctx.arc(x + 7, clipY + 6, 3.5, 0, Math.PI * 2);
        ctx.fill();
        ctx.beginPath();
        ctx.arc(x + w - 7, clipY + 6, 3.5, 0, Math.PI * 2);
        ctx.fill();

        ctx.fillStyle = palette.clipText;
        ctx.font = '9px Consolas, monospace';
        const label = clip.name || audioClipLabel;
        ctx.fillText(label, x + 4, row.y + 34);
      }

      if (selectedClipModel && selectedClipModel.track.id === row.track.id) {
        const selected = selectedClipModel.clip;
        for (const other of row.track.clips) {
          if (other.id === selected.id) continue;
          const overlapStart = Math.max(selected.startSample, other.startSample);
          const overlapEnd = Math.min(selected.startSample + selected.lengthSamples,
            other.startSample + other.lengthSamples);
          if (overlapEnd <= overlapStart) continue;
          const x = (overlapStart / sampleRate) * pixelsPerSecond;
          const w = Math.max(1, ((overlapEnd - overlapStart) / sampleRate) * pixelsPerSecond);
          ctx.fillStyle = palette.xfadeFill;
          ctx.fillRect(x, row.y + 20, w, row.height - 28);
          ctx.save();
          ctx.setLineDash([3, 3]);
          ctx.strokeStyle = palette.fade;
          ctx.strokeRect(x + 0.5, row.y + 20.5, Math.max(1, w - 1), row.height - 29);
          ctx.restore();
          ctx.fillStyle = palette.fade;
          ctx.font = '8px Consolas, monospace';
          ctx.fillText('XFADE', x + 4, row.y + row.height - 12);
        }
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
        ctx.fillStyle = palette.dragPreview;
        ctx.fillRect(x, row.y + 20, w, row.height - 28);
        ctx.globalAlpha = 0.95;
        ctx.setLineDash([4, 3]);
        ctx.strokeStyle = palette.fade;
        ctx.strokeRect(x + 0.5, row.y + 20.5, Math.max(1, w - 1), row.height - 29);
        ctx.setLineDash([]);
        if (sourceName) {
          ctx.fillStyle = palette.dragLabel;
          ctx.font = '9px Consolas, monospace';
          ctx.fillText(sourceName, x + 4, row.y + 34);
        }
        ctx.restore();
      }
    } else if (row.lane) {
      const lane = row.lane;
      ctx.fillStyle = palette.automationBackground;
      ctx.fillRect(0, row.y, width, row.height);
      ctx.strokeStyle = palette.automationLine;
      ctx.beginPath();
      ctx.moveTo(0, row.y);
      ctx.lineTo(width, row.y);
      ctx.stroke();
      const valueY = (value: number) => automationValueY(lane, row, value);
      const points = [...pointsForLane(row.track.id, lane)].sort((a, b) => a.sample - b.sample);
      if (points.length > 0) {
        ctx.strokeStyle = palette.automationCurve;
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
          ctx.fillStyle = active ? palette.automationPointActive : palette.automationPoint;
          ctx.beginPath();
          ctx.arc(pointX, valueY(point.value), active ? 7 : 5, 0, Math.PI * 2);
          ctx.fill();
          if (active) {
            const pointY = valueY(point.value);
            ctx.fillStyle = palette.labelBackground;
            ctx.fillRect(pointX + 9, pointY - 17, 76, 15);
            ctx.fillStyle = palette.labelText;
            ctx.fillText(point.value.toFixed(2), pointX + 14, pointY - 6);
          }
        }
      }
    }
  }

  for (const marker of markers) {
    const startX = (marker.startSample / sampleRate) * pixelsPerSecond;
    const endX = marker.endSample > marker.startSample ? (marker.endSample / sampleRate) * pixelsPerSecond : startX;
    if (marker.endSample > marker.startSample) {
      ctx.fillStyle = palette.regionFill;
      ctx.fillRect(startX, RULER_HEIGHT, Math.max(1, endX - startX), Math.max(1, height - RULER_HEIGHT));
      ctx.strokeStyle = palette.regionStroke;
      ctx.beginPath();
      ctx.moveTo(endX, 0);
      ctx.lineTo(endX, height);
      ctx.stroke();
    }

    ctx.strokeStyle = palette.marker;
    ctx.beginPath();
    ctx.moveTo(startX, 0);
    ctx.lineTo(startX, height);
    ctx.stroke();
    ctx.fillStyle = palette.marker;
    ctx.beginPath();
    ctx.moveTo(startX, 0);
    ctx.lineTo(startX + 7, 0);
    ctx.lineTo(startX, 8);
    ctx.closePath();
    ctx.fill();
    ctx.fillStyle = palette.markerText;
    ctx.font = '9px Consolas, monospace';
    ctx.fillText(marker.name, startX + 8, 12);
  }
  ctx.fillStyle = palette.playhead;
  ctx.beginPath();
  ctx.moveTo(playheadX - 5, 0);
  ctx.lineTo(playheadX + 5, 0);
  ctx.lineTo(playheadX, 8);
  ctx.fill();
  ctx.strokeStyle = palette.playhead;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.moveTo(playheadX, 0);
  ctx.lineTo(playheadX, height);
  ctx.stroke();
}
