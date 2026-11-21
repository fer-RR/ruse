import { useEffect, useMemo, useRef, useState } from 'react';
import { Circle, ClipboardPaste, Copy, Eye, Flag, Pause, Play, Plus, Redo2, Scissors, Square, Trash2, Undo2, X } from 'lucide-react';
import type {
  TimelineAutomationLaneModel,
  TimelineAutomationTarget,
  TimelineClipModel,
  TimelineState,
} from '../timeline';
import { useI18n } from '../i18n';
import { readTimelinePalette } from '../timelinePalette';
import { formatGainDb, nextVolumeValue } from '../gain';
import { withShortcut } from '../shortcuts';
import { ConfirmDialog, PromptDialog } from './Dialogs';
import TimelineContextMenu from './TimelineContextMenu';
import TimelineLabelOverlay, { type TimelineStickyLabel } from './TimelineLabelOverlay';
import { TrackGainSlider, TrackMeter } from './TimelineTrackControls';
import WelcomeTimelineAnimation from './WelcomeTimelineAnimation';

const BASE_PIXELS_PER_SECOND = 80;
const MAX_CANVAS_WIDTH = 16000;
const MIN_ZOOM = 0.001;
const MAX_ZOOM = 4;
const TRACK_HEIGHT = 72;
const AUTOMATION_HEIGHT = 48;

import {
  drawTimeline,
  RULER_HEIGHT,
  type ClipPreview,
  type DragKind,
  type TimelineAutomationDrag,
  type TimelineDragState,
  type TimelineRow,
} from './timelineCanvas';

type PendingRemoval =
  | { kind: 'automation'; trackId: string; laneId: string }
  | { kind: 'track'; trackId: string };

export interface TimelineClipUpdate {
  startSample?: number;
  lengthSamples?: number;
  sourceOffsetSamples?: number;
  gain?: number;
  pitchSemitones?: number;
  fadeInSamples?: number;
  fadeOutSamples?: number;
}

export default function TimelinePanel({
  state,
  automationTargets,
  showIntro,
  onDismissIntro,
  nodeIds,
  onPlay,
  onPause,
  onStop,
  onToggleRecord,
  onAddTrack,
  onImport,
  onSeek,
  onAddClipPath,
  onAddClipPaths,
  onAddGenerator,
  onMoveClip,
  onMoveClipToTrack,
  onUpdateClip,
  onStretchClip,
  onSplitClip,
  onDuplicateClip,
  onCrossfadeClip,
  onRemoveClip,
  onSetLoop,
  onSetTempo,
  onSetTimeSignature,
  onAddMarker,
  onUpdateMarker,
  onRemoveMarker,
  onCopyClips,
  onPasteClips,
  onNormalizeClip,
  onReverseClip,
  onConsolidateClips,
  onConsolidateRange,
  onBounce,
  onBounceRange,
  onToggleMetronome,
  onCountIn,
  onCancelBounce,
  onSetCountIn,
  onFreeze,
  onTrackUpdate,
  onAddAutomation,
  onSetAutomationPoints,
  onRemoveAutomation,
  onRemoveTrack,
  onRenameTrack,
  onShowTrackNode,
  onUndo,
  onRedo,
  onClose,
}: {
  state: TimelineState;
  automationTargets: TimelineAutomationTarget[];
  showIntro: boolean;
  onDismissIntro: () => void;
  nodeIds: Set<string>;
  onPlay: () => void;
  onPause: () => void;
  onStop: () => void;
  onToggleRecord: () => void;
  onAddTrack: () => void;
  onImport: () => void;
  onSeek: (sample: number) => void;
  onAddClipPath: (trackId: string, path: string, startSample: number) => void;
  onAddClipPaths: (trackId: string, paths: string[], startSample: number) => void;
  onAddGenerator: (trackId: string, nodeId: string, startSample: number) => void;
  onMoveClip: (trackId: string, clipId: string, startSample: number) => void;
  onMoveClipToTrack: (fromTrackId: string, toTrackId: string, clipId: string, startSample: number) => void;
  onUpdateClip: (trackId: string, clipId: string, update: TimelineClipUpdate) => void;
  onStretchClip: (trackId: string, clipId: string, lengthSamples: number) => void;
  onSplitClip: (trackId: string, clipId: string, sample: number) => void;
  onDuplicateClip: (trackId: string, clipId: string) => void;
  onCrossfadeClip: (trackId: string, clipId: string) => void;
  onRemoveClip: (trackId: string, clipId: string) => void;
  onSetLoop: (startSample: number | null, endSample: number | null) => void;
  onSetTempo: (tempo: number) => void;
  onSetTimeSignature: (numerator: number, denominator: number) => void;
  onAddMarker: (startSample: number, endSample?: number | null) => void;
  onUpdateMarker: (id: string, name: string) => void;
  onRemoveMarker: (id: string) => void;
  onCopyClips: (clips: { trackId: string; clipId: string }[]) => void;
  onPasteClips: (placements: { trackId: string; startSample: number }[]) => void;
  onNormalizeClip: (trackId: string, clipId: string) => void;
  onReverseClip: (trackId: string, clipId: string) => void;
  onConsolidateClips: (clips: { trackId: string; clipId: string }[]) => void;
  onConsolidateRange: (startSample: number, endSample: number) => void;
  onBounce: () => void;
  onBounceRange: () => void;
  onToggleMetronome: () => void;
  onCountIn: () => void;
  onCancelBounce: () => void;
  onSetCountIn: (value: number) => void;
  onFreeze: () => void;
  onTrackUpdate: (trackId: string, gain: number, muted: boolean, solo: boolean) => void;
  onAddAutomation: (trackId: string, target: TimelineAutomationTarget) => void;
  onSetAutomationPoints: (trackId: string, laneId: string, points: { sample: number; value: number; curve: number }[]) => void;
  onRemoveAutomation: (trackId: string, laneId: string) => void;
  onRemoveTrack: (trackId: string) => void;
  onRenameTrack: (trackId: string, name: string) => void;
  onShowTrackNode: (trackId: string, sourceNodeId?: string) => void;
  onUndo: () => void;
  onRedo: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();

  useEffect(() => {
    if (!showIntro) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      onDismissIntro();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [showIntro, onDismissIntro]);

  useEffect(() => {
    const root = document.documentElement;
    const update = () => setPalette(readTimelinePalette());
    const observer = new MutationObserver(update);
    observer.observe(root, { attributes: true, attributeFilter: ['data-theme'] });
    update();
    return () => observer.disconnect();
  }, []);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const panRef = useRef<{ clientX: number; clientY: number; scrollLeft: number; scrollTop: number } | null>(null);
  const [zoom, setZoom] = useState(1);
  const [rowZoom, setRowZoom] = useState(() => {
    const raw = localStorage.getItem('ruse.timelineRowZoom');
    const saved = Number(raw);
    return raw != null && Number.isFinite(saved) ? Math.max(0.5, Math.min(2, saved)) : 1;
  });
  const [palette, setPalette] = useState(readTimelinePalette);
  const [panning, setPanning] = useState(false);
  const [seeking, setSeeking] = useState(false);
  const [dropActive, setDropActive] = useState(false);
  const [selectedClip, setSelectedClip] = useState<{ trackId: string; clipId: string } | null>(null);
  const [selectedClipKeys, setSelectedClipKeys] = useState<string[]>([]);
  const [clipboard, setClipboard] = useState<{ items: { trackId: string; clipId: string; offset: number }[] } | null>(null);
  const [markerMenu, setMarkerMenu] = useState<{ x: number; y: number; markerId: string } | null>(null);
  const [markerRename, setMarkerRename] = useState<{ id: string; name: string } | null>(null);
  const [snap, setSnap] = useState('0.1');
  const [tempoDraft, setTempoDraft] = useState('120');
  const [drag, setDrag] = useState<TimelineDragState | null>(null);
  const [automationDrag, setAutomationDrag] = useState<TimelineAutomationDrag | null>(null);
  const [pendingRemoval, setPendingRemoval] = useState<PendingRemoval | null>(null);
  const [clipMenu, setClipMenu] = useState<{ x: number; y: number; trackId: string; clipId: string } | null>(null);
  const [timelineMenu, setTimelineMenu] = useState<{ x: number; y: number; sample: number } | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const timelineResizeCleanup = useRef<(() => void) | null>(null);
  useEffect(() => () => timelineResizeCleanup.current?.(), []);
  const [follow, setFollow] = useState(() => localStorage.getItem('ruse.timelineFollow') !== '0');
  const [dockHeight, setDockHeight] = useState(() => {
    const saved = Number(localStorage.getItem('ruse.timelineHeight'));
    return Number.isFinite(saved) && saved >= 180 ? Math.min(saved, Math.max(220, window.innerHeight - 160)) : 340;
  });
  const [loopDrag, setLoopDrag] = useState<{ startSample: number; currentSample: number } | null>(null);

  useEffect(() => {
    if (!clipMenu && !timelineMenu && !markerMenu) return;
    const closeMenus = () => {
      setClipMenu(null);
      setTimelineMenu(null);
      setMarkerMenu(null);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        closeMenus();
      } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        const items = Array.from(menuRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)') ?? []);
        if (items.length === 0) return;
        event.preventDefault();
        const current = items.indexOf(document.activeElement as HTMLButtonElement);
        const delta = event.key === 'ArrowDown' ? 1 : -1;
        const next = current < 0 ? 0 : (current + delta + items.length) % items.length;
        items[next].focus();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    const first = menuRef.current?.querySelector<HTMLButtonElement>('[role="menuitem"]:not(:disabled)');
    first?.focus();
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [clipMenu, timelineMenu, markerMenu]);

  const tracks = state.project.tracks;
  const sampleRate = state.sampleRate;
  const selectedClipModel = useMemo(() => {
    if (!selectedClip) return null;
    const track = tracks.find(item => item.id === selectedClip.trackId);
    const clip = track?.clips.find(item => item.id === selectedClip.clipId);
    return track && clip ? { track, clip } : null;
  }, [selectedClip, tracks]);
  const orderedClips = useMemo(() => tracks
    .flatMap((track, trackIndex) => track.clips.map(clip => ({ track, clip, trackIndex })))
    .sort((a, b) => a.clip.startSample - b.clip.startSample || a.trackIndex - b.trackIndex), [tracks]);
  const canSplitSelected = selectedClipModel != null &&
    state.playheadSample > selectedClipModel.clip.startSample &&
    state.playheadSample < selectedClipModel.clip.startSample + selectedClipModel.clip.lengthSamples;
  const canCrossfadeSelected = selectedClipModel != null && tracks.some(track =>
    track.id === selectedClipModel.track.id && track.clips.some(other =>
      other.id !== selectedClipModel.clip.id &&
      other.startSample < selectedClipModel.clip.startSample + selectedClipModel.clip.lengthSamples &&
      other.startSample + other.lengthSamples > selectedClipModel.clip.startSample));
  const canPitchSelected = selectedClipModel != null &&
    !selectedClipModel.track.generatorNodeId &&
    Boolean(selectedClipModel.clip.sourcePath);
  const menuClipModel = useMemo(() => {
    if (!clipMenu) return null;
    const track = tracks.find(item => item.id === clipMenu.trackId);
    const clip = track?.clips.find(item => item.id === clipMenu.clipId);
    return track && clip ? { track, clip } : null;
  }, [clipMenu, tracks]);
  const canSplitMenu = menuClipModel != null &&
    state.playheadSample > menuClipModel.clip.startSample &&
    state.playheadSample < menuClipModel.clip.startSample + menuClipModel.clip.lengthSamples;
  const canCrossfadeMenu = menuClipModel != null && tracks.some(track =>
    track.id === menuClipModel.track.id && track.clips.some(other =>
      other.id !== menuClipModel.clip.id &&
      other.startSample < menuClipModel.clip.startSample + menuClipModel.clip.lengthSamples &&
      other.startSample + other.lengthSamples > menuClipModel.clip.startSample));
  const canPitchMenu = menuClipModel != null &&
    !menuClipModel.track.generatorNodeId &&
    Boolean(menuClipModel.clip.sourcePath);
  const canProcessMenu = menuClipModel != null && Boolean(menuClipModel.clip.sourcePath);
  const clipKey = (trackId: string, clipId: string) => `${trackId}:${clipId}`;
  const selectedClipEntries = useMemo(() => selectedClipKeys.flatMap(key => {
    const separator = key.indexOf(':');
    const trackId = key.slice(0, separator);
    const clipId = key.slice(separator + 1);
    const track = tracks.find(item => item.id === trackId);
    const clip = track?.clips.find(item => item.id === clipId);
    return track && clip ? [{ trackId, clipId, track, clip }] : [];
  }), [selectedClipKeys, tracks]);
  const consolidateClipEntries = (() => {
    if (!menuClipModel) {
      return selectedClipEntries;
    }

    const clickedSelected = selectedClipEntries.some(entry =>
      entry.trackId === menuClipModel.track.id && entry.clipId === menuClipModel.clip.id);
    return clickedSelected
      ? selectedClipEntries
      : [{
          trackId: menuClipModel.track.id,
          clipId: menuClipModel.clip.id,
          track: menuClipModel.track,
          clip: menuClipModel.clip,
        }];
  })();

  const selectClip = (trackId: string, clipId: string, additive: boolean) => {
    const key = clipKey(trackId, clipId);
    if (additive) {
      setSelectedClipKeys(current => current.includes(key)
        ? current.filter(item => item !== key)
        : [...current, key]);
      setSelectedClip(current => current && current.trackId === trackId && current.clipId === clipId
        ? null
        : { trackId, clipId });
      return;
    }

    setSelectedClipKeys([key]);
    setSelectedClip({ trackId, clipId });
  };

  const copySelection = () => {
    const entries = selectedClipEntries.length > 0
      ? selectedClipEntries
      : selectedClipModel ? [{ ...selectedClipModel, trackId: selectedClipModel.track.id, clipId: selectedClipModel.clip.id }] : [];
    if (entries.length === 0) return;
    onCopyClips(entries.map(entry => ({ trackId: entry.trackId, clipId: entry.clipId })));
    const anchor = Math.min(...entries.map(entry => entry.clip.startSample));
    setClipboard({
      items: entries.map(entry => ({
        trackId: entry.trackId,
        clipId: entry.clipId,
        offset: entry.clip.startSample - anchor,
      })),
    });
  };

  const removeSelection = () => {
    for (const entry of selectedClipEntries) {
      onRemoveClip(entry.trackId, entry.clipId);
    }
    setSelectedClipKeys([]);
    setSelectedClip(null);
  };

  const cutSelection = () => {
    copySelection();
    removeSelection();
  };

  const pasteClipboard = () => {
    if (!clipboard || clipboard.items.length === 0) return;
    const trackIds = new Set(tracks.map(track => track.id));
    const fallbackTrack = selectedClip?.trackId ?? tracks[0]?.id;
    const placements = clipboard.items.flatMap(item => {
      const trackId = trackIds.has(item.trackId) ? item.trackId : fallbackTrack;
      return trackId
        ? [{ trackId, startSample: Math.max(0, state.playheadSample + item.offset) }]
        : [];
    });
    if (placements.length > 0) onPasteClips(placements);
  };

  const [clipGain, setClipGain] = useState(1);
  const clipGainTimer = useRef<number | null>(null);
  const clipGainPending = useRef<number | null>(null);
  const clipGainTarget = useRef<{ trackId: string; clipId: string } | null>(null);
  const clipGainDirtyUntil = useRef(0);
  const flushClipGain = () => {
    if (clipGainTimer.current !== null) {
      window.clearTimeout(clipGainTimer.current);
      clipGainTimer.current = null;
    }
    const pending = clipGainPending.current;
    const target = clipGainTarget.current;
    clipGainPending.current = null;
    clipGainTarget.current = null;
    if (pending !== null && target) {
      onUpdateClip(target.trackId, target.clipId, { gain: pending });
    }
  };
  useEffect(() => {
    if (selectedClipModel && Date.now() >= clipGainDirtyUntil.current) {
      setClipGain(selectedClipModel.clip.gain);
    }
  }, [selectedClipModel?.clip.id, selectedClipModel?.clip.gain]);
  useEffect(() => () => flushClipGain(), []);
  useEffect(() => {
    flushClipGain();
    clipGainDirtyUntil.current = 0;
  }, [selectedClip?.clipId]);
  const queueClipGain = (value: number, immediate = false) => {
    if (!selectedClipModel) return;
    clipGainDirtyUntil.current = Date.now() + 400;
    setClipGain(value);
    clipGainPending.current = value;
    clipGainTarget.current = { trackId: selectedClipModel.track.id, clipId: selectedClipModel.clip.id };
    if (immediate) {
      flushClipGain();
    } else if (clipGainTimer.current === null) {
      clipGainTimer.current = window.setTimeout(flushClipGain, 50);
    }
  };

  useEffect(() => {
    localStorage.setItem('ruse.timelineFollow', follow ? '1' : '0');
  }, [follow]);

  useEffect(() => {
    localStorage.setItem('ruse.timelineRowZoom', String(rowZoom));
  }, [rowZoom]);


  useEffect(() => {
    const workspace = scrollRef.current?.closest('.workspace') as HTMLElement | null;
    workspace?.style.setProperty('--timeline-dock-height', `${dockHeight}px`);
    localStorage.setItem('ruse.timelineHeight', String(dockHeight));
  }, [dockHeight]);

  useEffect(() => {
    const clampHeight = () => setDockHeight(value => Math.min(value, Math.max(180, window.innerHeight - 160)));
    window.addEventListener('resize', clampHeight);
    return () => window.removeEventListener('resize', clampHeight);
  }, []);

  const trackHeight = Math.max(36, Math.round(TRACK_HEIGHT * rowZoom));
  const automationHeight = Math.max(28, Math.round(AUTOMATION_HEIGHT * rowZoom));
  const rows = useMemo(() => {
    const result: TimelineRow[] = [];
    let rowY = RULER_HEIGHT;
    for (let index = 0; index < state.project.tracks.length; index++) {
      const track = state.project.tracks[index];
      result.push({ kind: 'track', index, track, y: rowY, height: trackHeight });
      rowY += trackHeight;
      for (const lane of track.automation ?? []) {
        result.push({ kind: 'automation', index, track, lane, y: rowY, height: automationHeight });
        rowY += automationHeight;
      }
    }

    return result;
  }, [state.project.tracks, trackHeight, automationHeight]);

  const stickyLabels = useMemo(() => rows.flatMap((row): TimelineStickyLabel[] => {
    if (row.kind === 'track') {
      return [{
        id: `label-${row.track.id}`,
        top: row.y + 3,
        text: row.track.name,
        kind: 'track' as const,
        muted: row.track.muted,
        solo: row.track.solo,
      }];
    }

    if (row.lane) {
      const target = automationTargets.find(item => item.nodeId === row.lane?.targetNodeId &&
        item.processorIndex === row.lane?.processorIndex &&
        item.parameterIndex === row.lane?.parameterIndex);
      const min = target?.min ?? 0;
      const max = target?.max ?? 1;
      return [{
        id: `label-${row.lane.id}`,
        top: row.y + 3,
        text: `${target?.label ?? t('Unknown parameter')}  ${min.toFixed(2)}..${max.toFixed(2)}`,
        kind: 'automation' as const,
      }];
    }

    return [];
  }), [automationTargets, rows, t]);

  const maxEnd = Math.max(
    sampleRate * 2,
    tracks.reduce((max, track) => track.clips.reduce((clipMax, clip) =>
      Math.max(clipMax, clip.startSample + clip.lengthSamples), max), 0),
    tracks.reduce((max, track) => (track.automation ?? []).reduce((laneMax, lane) =>
      lane.points.reduce((pointMax, point) => Math.max(pointMax, point.sample), laneMax), max), 0),
  );
  const seconds = Math.max(12, maxEnd / sampleRate + 2);
  const pixelsPerSecond = Math.min(BASE_PIXELS_PER_SECOND * zoom, MAX_CANVAS_WIDTH / seconds);

  useEffect(() => {
    if (!follow || !state.playing) return;
    const element = scrollRef.current;
    if (!element) return;
    const x = (state.playheadSample / sampleRate) * pixelsPerSecond;
    const margin = 48;
    if (x < element.scrollLeft + margin || x > element.scrollLeft + element.clientWidth - margin) {
      element.scrollLeft = Math.max(0, x - element.clientWidth * 0.25);
    }
  }, [follow, pixelsPerSecond, sampleRate, state.playheadSample, state.playing]);
  const width = Math.max(320, Math.ceil(seconds * pixelsPerSecond));
  const height = Math.max(RULER_HEIGHT + trackHeight,
    rows.length > 0 ? rows[rows.length - 1].y + rows[rows.length - 1].height : RULER_HEIGHT);
  const playheadX = (state.playheadSample / sampleRate) * pixelsPerSecond;
  const loopActive = state.project.loopEndSample > state.project.loopStartSample;
  const displayLoopStart = loopDrag ? Math.min(loopDrag.startSample, loopDrag.currentSample) : state.project.loopStartSample;
  const displayLoopEnd = loopDrag ? Math.max(loopDrag.startSample, loopDrag.currentSample) : state.project.loopEndSample;
  const displayLoopActive = displayLoopEnd > displayLoopStart;
  const tempo = state.project.tempo > 0 ? state.project.tempo : 120;
  const beatsPerBar = Math.max(1, Math.round(state.project.timeSignatureNumerator || 4));
  const timeSignatureDenominator = Math.max(1, Math.round(state.project.timeSignatureDenominator || 4));
  const beatSeconds = (60 / tempo) * (4 / timeSignatureDenominator);
  const playheadBeats = state.playheadSample / sampleRate / beatSeconds;
  const positionBar = Math.floor(playheadBeats / beatsPerBar) + 1;
  const positionBeat = Math.floor(playheadBeats % beatsPerBar) + 1;
  const positionTick = Math.floor((playheadBeats % 1) * 1000);
  const snapSamples = snap === 'off'
    ? 0
    : snap === 'beat'
      ? Math.max(1, Math.round(beatSeconds * sampleRate))
      : snap === 'bar'
        ? Math.max(1, Math.round(beatsPerBar * beatSeconds * sampleRate))
        : Math.max(1, Math.round(Number(snap) * sampleRate));

  const snapSample = (sample: number) => snapSamples > 0
    ? Math.max(0, Math.round(sample / snapSamples) * snapSamples)
    : Math.max(0, Math.round(sample));

  const snapToEdges = (sample: number, excludeClipId?: string) => {
    let best = snapSample(sample);
    let bestDistance = Math.abs(best - sample);
    const threshold = Math.max(1, (8 / pixelsPerSecond) * sampleRate);
    const consider = (edge: number) => {
      const distance = Math.abs(edge - sample);
      if (distance <= threshold && distance < bestDistance) {
        best = Math.max(0, Math.round(edge));
        bestDistance = distance;
      }
    };

    for (const track of tracks) {
      for (const clip of track.clips) {
        if (clip.id === excludeClipId) continue;
        consider(clip.startSample);
        consider(clip.startSample + clip.lengthSamples);
      }
    }
    consider(state.playheadSample);
    return best;
  };

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
      if (event.altKey) {
        event.preventDefault();
        setRowZoom(current => Math.max(0.5, Math.min(2,
          current * (event.deltaY > 0 ? 0.9 : 1.1))));
      } else if (event.shiftKey && !event.ctrlKey) {
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
    let bestDistance = 14;
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
    const frame = requestAnimationFrame(() => {
      drawTimeline(canvas, {
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
        markers: state.project.markers ?? [],
        playheadX,
        drawPlayhead: false,
        audioClipLabel: t('Audio clip'),
      });
    });
    return () => cancelAnimationFrame(frame);
  }, [automationDrag, automationTargets, beatsPerBar, beatSeconds, drag, displayLoopActive, displayLoopEnd, displayLoopStart, height, palette, pixelsPerSecond, rows, sampleRate, seconds, selectedClipKeys, selectedClipModel, state.project.markers, t, tracks, width]);


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

  const markerAtX = (clientX: number) => {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const rect = canvas.getBoundingClientRect();
    const x = clientX - rect.left;
    return (state.project.markers ?? []).find(marker =>
      Math.abs((marker.startSample / sampleRate) * pixelsPerSecond - x) <= 7) ?? null;
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
        const sourceTrack = tracks.find(track => track.id === drag.trackId);
        const sourceGenerator = sourceTrack?.generatorNodeId;
        const targetGenerator = target.track.generatorNodeId;
        targetTrackId = (sourceGenerator || targetGenerator) && sourceGenerator !== targetGenerator
          ? drag.trackId
          : target.track.id;
      }
      preview.startSample = Math.max(0, snapToEdges(original.startSample + deltaSamples, drag.clipId));
    } else if (drag.kind === 'resize-left') {
      const nextStart = Math.max(0, Math.min(
        original.startSample + original.lengthSamples - 1,
        snapToEdges(original.startSample + deltaSamples, drag.clipId),
      ));
      const trim = nextStart - original.startSample;
      preview.startSample = nextStart;
      preview.sourceOffsetSamples = Math.max(0, original.sourceOffsetSamples + trim);
      preview.lengthSamples = Math.max(1, original.lengthSamples - trim);
      preview.fadeInSamples = Math.min(preview.fadeInSamples, preview.lengthSamples);
      preview.fadeOutSamples = Math.min(preview.fadeOutSamples, preview.lengthSamples);
    } else if (drag.kind === 'resize-right') {
      const nextEnd = snapToEdges(original.startSample + original.lengthSamples + deltaSamples, drag.clipId);
      preview.lengthSamples = Math.max(1, nextEnd - original.startSample);
      preview.fadeInSamples = Math.min(preview.fadeInSamples, preview.lengthSamples);
      preview.fadeOutSamples = Math.min(preview.fadeOutSamples, preview.lengthSamples);
    } else if (drag.kind === 'stretch-right') {
      const nextEnd = snapToEdges(original.startSample + original.lengthSamples + deltaSamples, drag.clipId);
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
      <div className="timeline-resize-handle" role="separator" aria-orientation="horizontal" aria-label={t('Resize timeline')} title={t('Resize timeline')} onDoubleClick={() => setDockHeight(340)} onPointerDown={event => {
        event.preventDefault();
        const startY = event.clientY;
        const startHeight = dockHeight;
        const move = (moveEvent: PointerEvent) => {
          setDockHeight(Math.max(180, Math.min(window.innerHeight - 160, startHeight + startY - moveEvent.clientY)));
        };
        const up = () => {
          window.removeEventListener('pointermove', move);
          window.removeEventListener('pointerup', up);
          window.removeEventListener('pointercancel', up);
          timelineResizeCleanup.current = null;
        };
        timelineResizeCleanup.current?.();
        timelineResizeCleanup.current = up;
        window.addEventListener('pointermove', move);
        window.addEventListener('pointerup', up);
        window.addEventListener('pointercancel', up);
      }} />

      <header className="timeline-header">
        <strong>{t('Timeline')}</strong>
        <span className="timeline-position" title={t('Bar, beat and time')}>
          {positionBar}.{positionBeat}.{String(positionTick).padStart(3, '0')}
          <small>{(state.playheadSample / sampleRate).toFixed(2)}s</small>
        </span>
        <div className="timeline-header-end">
          <div className="timeline-zoom" role="group" aria-label={t('Timeline zoom')}>
            <button className="icon-button compact" onClick={() => zoomBy(0.5)} title={t('Zoom out')} aria-label={t('Zoom out')}>-</button>
            <button className="icon-button compact" onClick={() => zoomBy(2)} title={t('Zoom in')} aria-label={t('Zoom in')}>+</button>
            <select
              className="timeline-row-zoom"
              value={rowZoom}
              onChange={event => setRowZoom(Number(event.target.value))}
              aria-label={t('Track height')}
              title={t('Track height')}
            >
              {[0.5, 0.75, 1, 1.25, 1.5, 2].map(value => (
                <option key={value} value={value}>{Math.round(value * 100)}%</option>
              ))}
            </select>
            <button className="tool-button" onClick={fitTimeline} title={t('Fit timeline')}>{t('Fit')}</button>
            <button className={`tool-button ${follow ? 'active' : ''}`} aria-pressed={follow} title={t('Follow playhead')} onClick={() => setFollow(value => !value)}>{t('Follow')}</button>
            <button className={`tool-button ${state.metronome ? 'active' : ''}`} aria-pressed={state.metronome === true} onClick={onToggleMetronome} title={t('Transport click')}>{t('Transport click')}</button>
            <button className="tool-button" disabled={state.playing} onClick={onCountIn}>{t('Count-in')}</button>
            <select className="timeline-count-in-beats" value={state.countInBeats ?? 4} onChange={event => onSetCountIn(Number(event.target.value))} aria-label={t('Count-in beats')} title={t('Count-in beats')}>
              {[2, 3, 4, 6].map(value => <option key={value} value={value}>{value}</option>)}
            </select>
          </div>
          <button className="icon-button compact timeline-close" onClick={onClose} title={t('Close timeline')} aria-label={t('Close timeline')}><X size={13} /></button>
        </div>
        <div className="timeline-actions">
          <button className="icon-button compact" onClick={onPlay} disabled={state.playing} title={withShortcut(t('Play'), 'timelinePlay')} aria-label={t('Play')}>
            <Play size={13} fill="currentColor" />
          </button>
          <button className="icon-button compact" onClick={onPause} disabled={!state.playing} title={withShortcut(t('Pause'), 'timelinePlay')} aria-label={t('Pause')}>
            <Pause size={13} fill="currentColor" />
          </button>
          <button className="icon-button compact" onClick={onStop} disabled={!state.playing && state.playheadSample <= 0} title={t('Stop')} aria-label={t('Stop')}>
            <Square size={12} fill="currentColor" />
          </button>
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
          <label className="timeline-time-signature" title={t('Time signature')}>
            <select
              value={state.project.timeSignatureNumerator ?? 4}
              onChange={event => onSetTimeSignature(Number(event.target.value), state.project.timeSignatureDenominator ?? 4)}
              aria-label={t('Time signature numerator')}
            >
              {[2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16].map(value => <option key={value} value={value}>{value}</option>)}
            </select>
            <span>/</span>
            <select
              value={state.project.timeSignatureDenominator ?? 4}
              onChange={event => onSetTimeSignature(state.project.timeSignatureNumerator ?? 4, Number(event.target.value))}
              aria-label={t('Time signature denominator')}
            >
              {[1, 2, 4, 8, 16].map(value => <option key={value} value={value}>{value}</option>)}
            </select>
          </label>
          <button className="icon-button compact" onClick={() => onAddMarker(state.playheadSample)} title={t('Add marker at playhead')} aria-label={t('Marker')}><Flag size={13} /></button>
          <button className="icon-button compact" disabled={!loopActive} onClick={() => onAddMarker(Math.min(displayLoopStart, displayLoopEnd), Math.max(displayLoopStart, displayLoopEnd))} title={loopActive ? t('Add region from loop range') : t('Set a loop range first')} aria-label={t('Region')}><Square size={13} /></button>
          {selectedClipModel && (
            <label className="timeline-clip-gain">
              <span>{t('Clip gain')}</span>
              <span className="volume-slider">
                <input
                  type="range"
                  min={0}
                  max={4}
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
          {selectedClipModel && <>
            <button className="tool-button" disabled={!canSplitSelected} title={t('Split')} onClick={() => onSplitClip(selectedClipModel.track.id, selectedClipModel.clip.id, state.playheadSample)}>{t('Split')}</button>
            <button className="tool-button" onClick={() => onDuplicateClip(selectedClipModel.track.id, selectedClipModel.clip.id)}>{t('Duplicate')}</button>
            <button className="tool-button" disabled={!canCrossfadeSelected} onClick={() => onCrossfadeClip(selectedClipModel.track.id, selectedClipModel.clip.id)}>{t('Crossfade')}</button>
            <label className="timeline-clip-length">
              <span>{t('Length')}</span>
              <input
                key={selectedClipModel.clip.id}
                type="number"
                min={10}
                step={10}
                defaultValue={Math.max(1, Math.round(selectedClipModel.clip.lengthSamples / sampleRate * 1000))}
                onBlur={event => {
                  const ms = Number(event.target.value);
                  if (Number.isFinite(ms) && ms > 0) onUpdateClip(selectedClipModel.track.id, selectedClipModel.clip.id, { lengthSamples: Math.max(1, Math.round(ms * sampleRate / 1000)) });
                }}
                onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur(); }}
              />
              <span>ms</span>
            </label>
            {canPitchSelected && <label className="timeline-clip-pitch">
              <span>{t('Clip pitch')}</span>
              <input
                key={`${selectedClipModel.clip.id}-${selectedClipModel.clip.pitchSemitones}`}
                type="number"
                min={-24}
                max={24}
                step={1}
                defaultValue={selectedClipModel.clip.pitchSemitones ?? 0}
                onBlur={event => {
                  const semitones = Number(event.target.value);
                  if (Number.isFinite(semitones)) {
                    onUpdateClip(selectedClipModel.track.id, selectedClipModel.clip.id, {
                      pitchSemitones: Math.max(-24, Math.min(24, semitones)),
                    });
                  }
                }}
                onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur(); }}
              />
              <span>st</span>
            </label>}

          </>}
          {selectedClipEntries.length > 0 && <>
            <button className="icon-button compact" title={t('Copy clips')} aria-label={t('Copy clips')} onClick={copySelection}><Copy size={13} /></button>
            <button className="icon-button compact" title={t('Cut clips')} aria-label={t('Cut clips')} onClick={cutSelection}><Scissors size={13} /></button>
          </>}
          {clipboard && <button className="icon-button compact" title={t('Paste clips')} aria-label={t('Paste clips')} onClick={pasteClipboard}><ClipboardPaste size={13} /></button>}
          <button className="icon-button compact" onClick={onUndo} disabled={!state.canUndo} title={withShortcut(t('Undo'), 'undo')} aria-label={t('Undo')}>
            <Undo2 size={13} />
          </button>
          <button className="icon-button compact" onClick={onRedo} disabled={!state.canRedo} title={withShortcut(t('Redo'), 'redo')} aria-label={t('Redo')}>
            <Redo2 size={13} />
          </button>
          <button
            className={`icon-button compact timeline-record-button ${state.recording ? 'recording' : ''}`}
            aria-pressed={state.recording === true}
            aria-label={state.recording ? t('Stop record') : t('Record')}
            title={state.recording ? t('Stop record') : t('Record')}
            onClick={onToggleRecord}
          >
            <Circle size={12} fill="currentColor" />
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
          <button className="tool-button" onClick={onImport}>{t('Import clip')}</button>
          {state.bouncing && <span className="timeline-bounce-progress">{Math.round((state.bounceProgress ?? 0) * 100)}% <button className="tool-button danger" onClick={onCancelBounce}>{t('Cancel')}</button></span>}
          <button className="tool-button" onClick={onBounce}>{t('Bounce mix')}</button>
          <button className="tool-button" disabled={!loopActive} title={loopActive ? t('Bounce range') : t('Shift+drag the ruler to set a loop range')} onClick={onBounceRange}>{t('Bounce range')}</button>
          <button className="tool-button" onClick={onFreeze}>{t('Freeze')}</button>
        </div>
      </header>
      {showIntro && (
        <div className="timeline-intro" role="dialog" aria-modal="true" aria-label={t('Build with the timeline')}>
          <WelcomeTimelineAnimation className="timeline-intro-animation" />
          <div className="timeline-intro-copy">
            <strong>{t('Build with the timeline')}</strong>
            <p>{t('Open the Timeline, add a track, drop samples or recordings, then press Space to play. Drag an effect onto a cable to build an effect chain.')}</p>
            <button className="primary-button" autoFocus onClick={onDismissIntro}>{t('Got it')}</button>
          </div>
        </div>
      )}

      {tracks.length > 0 && (
        <div className="timeline-track-strip">
          {tracks.map(track => {
            const trackNodeId = track.sourceNodeId ?? track.generatorNodeId;
            const hasNode = Boolean(trackNodeId && nodeIds.has(trackNodeId));
            const trackTargets = automationTargets.filter(target =>
              !target.trackId || target.trackId === track.id);
            const ownTargets = trackTargets.filter(target => target.section !== 'shared');
            const sharedTargets = trackTargets.filter(target => target.section === 'shared');
            return (
            <div className="timeline-track-control" key={track.id}>
              <div className="timeline-track-heading">
                <strong
                  className="timeline-track-name"
                  title={t('Double-click to rename')}
                  onDoubleClick={() => onRenameTrack(track.id, track.name)}
                >{track.name}</strong>
                {!trackNodeId && <small className="timeline-track-no-source">{t('No source')}</small>}
                <span className="timeline-track-actions">
                  <button
                    className="icon-button compact"
                    title={hasNode ? t('Show track node') : t('Restore track node')}
                    aria-label={hasNode ? t('Show track node') : t('Restore track node')}
                    onClick={() => onShowTrackNode(track.id, trackNodeId)}
                  >{hasNode ? <Eye size={13} /> : <Plus size={13} />}</button>
                  <button
                    className="icon-button compact danger-text"
                    title={t('Remove track')}
                    aria-label={t('Remove track')}
                    onClick={() => setPendingRemoval({ kind: 'track', trackId: track.id })}
                  ><Trash2 size={13} /></button>
                </span>
              </div>
              <div className="timeline-track-mix">
                <TrackMeter nodeId={trackNodeId} />
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
                    const target = trackTargets[Number(event.target.value)];
                    if (target) onAddAutomation(track.id, target);
                  }}
                >
                  <option value="">+ A</option>
                  {ownTargets.length > 0 && <optgroup label={t('Track')}>
                    {ownTargets.map(target => (
                      <option value={trackTargets.indexOf(target)} key={`${target.nodeId}-${target.processorIndex}-${target.parameterIndex}`}>
                        {target.label}
                      </option>
                    ))}
                  </optgroup>}
                  {sharedTargets.length > 0 && <optgroup label={t('Shared buses')}>
                    {sharedTargets.map(target => (
                      <option value={trackTargets.indexOf(target)} key={`${target.nodeId}-${target.processorIndex}-${target.parameterIndex}`}>
                        {target.label}
                      </option>
                    ))}
                  </optgroup>}
                </select>
              </div>
            </div>
          );
          })}
          <button className="timeline-add-track-card" onClick={onAddTrack} title={t('Add track')} aria-label={t('Add track')}>
            <Plus size={22} />
            <small>{t('Add track')}</small>
          </button>
        </div>
      )}
      <div
        className={`timeline-scroll ${panning ? 'panning' : ''} ${seeking ? 'seeking' : ''} ${dropActive ? 'drop-active' : ''}`}
        ref={scrollRef}
        onDragOver={event => {
          const types = Array.from(event.dataTransfer.types);
          if (types.includes('Files') || types.includes('application/ruse-item') ||
              types.includes('application/ruse-recording') ||
              types.includes('application/ruse-media-list') ||
              types.includes('application/ruse-generator')) {
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
          const generatorData = event.dataTransfer.getData('application/ruse-generator');
          if (generatorData) {
            try {
              const generator = JSON.parse(generatorData) as { id?: string };
              if (generator.id) onAddGenerator(trackId, generator.id, sample);
            } catch {
            }
            return;
          }

          if (files.length > 0) {
            window.chrome?.webview?.postMessageWithAdditionalObjects?.(
              { type: 'timeline-file-drop', payload: { trackId, startSample: sample } },
              files,
            );
            return;
          }

          const mediaListRaw = event.dataTransfer.getData('application/ruse-media-list');
          if (mediaListRaw) {
            try {
              const parsed: unknown = JSON.parse(mediaListRaw);
              if (Array.isArray(parsed)) {
                const paths = parsed.filter((path): path is string => typeof path === 'string' && path.length > 0);
                if (paths.length > 0) onAddClipPaths(trackId, paths, sample);
              }
            } catch {
            }
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
          <>
          <div className="timeline-canvas-wrap">
          <TimelineLabelOverlay labels={stickyLabels} />
          <div className="sr-only" role="status" aria-live="polite">
            {selectedClipModel
              ? `${t('Selected clip')}: ${selectedClipModel.clip.name} \u00B7 ${t('Track')}: ${selectedClipModel.track.name}`
              : ''}
          </div>
          <canvas
            ref={canvasRef}
            tabIndex={0}
            role="application"
            aria-label={t('Timeline')}
            aria-keyshortcuts="Control+C Control+X Control+V Control+A Control+ArrowLeft Control+ArrowRight Delete Backspace ArrowLeft ArrowRight Home End"
            title={`${t('Shift+drag a clip edge to time-stretch')} \u00B7 ${t('Shift+drag the ruler to set a loop range')}`}
            onKeyDown={event => {
              const mod = event.ctrlKey || event.metaKey;
              const selectedTrack = selectedClip
                ? tracks.find(track => track.id === selectedClip.trackId)
                : undefined;
              const selected = selectedClip
                ? selectedTrack?.clips.find(clip => clip.id === selectedClip.clipId)
                : undefined;
              if (mod && event.key.toLowerCase() === 'a') {
                event.preventDefault();
                const keys = tracks.flatMap(track => track.clips.map(clip => clipKey(track.id, clip.id)));
                setSelectedClipKeys(keys);
                const first = keys[0];
                setSelectedClip(first
                  ? { trackId: first.slice(0, first.indexOf(':')), clipId: first.slice(first.indexOf(':') + 1) }
                  : null);
              } else if (mod && event.key.toLowerCase() === 'c') {
                event.preventDefault();
                copySelection();
              } else if (mod && event.key.toLowerCase() === 'x') {
                event.preventDefault();
                cutSelection();
              } else if (mod && event.key.toLowerCase() === 'v') {
                event.preventDefault();
                pasteClipboard();
              } else if (mod && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
                event.preventDefault();
                if (orderedClips.length === 0) return;
                const currentIndex = orderedClips.findIndex(entry =>
                  entry.track.id === selectedClip?.trackId && entry.clip.id === selectedClip?.clipId);
                const direction = event.key === 'ArrowRight' ? 1 : -1;
                const nextIndex = currentIndex < 0
                  ? 0
                  : (currentIndex + direction + orderedClips.length) % orderedClips.length;
                const entry = orderedClips[nextIndex];
                setSelectedClip({ trackId: entry.track.id, clipId: entry.clip.id });
                setSelectedClipKeys([clipKey(entry.track.id, entry.clip.id)]);
              } else if ((event.key === 'Delete' || event.key === 'Backspace') && (selectedClipKeys.length > 0 || selectedClip)) {
                event.preventDefault();
                removeSelection();
              } else if (event.key === 'Escape') {
                setSelectedClip(null);
                setSelectedClipKeys([]);
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
                  clientY: event.clientY,
                  scrollLeft: scrollRef.current?.scrollLeft ?? 0,
                  scrollTop: scrollRef.current?.scrollTop ?? 0,
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
                const snapped = snapToEdges(sample);
                if (event.shiftKey) {
                  setLoopDrag({ startSample: snapped, currentSample: snapped });
                } else {
                  setSeeking(true);
                  onSeek(snapped);
                }
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
                setSelectedClipKeys([]);
                onSeek(snapSample(hit.sample));
                return;
              }

              selectClip(hit.row.track.id, clip.clip.id, event.ctrlKey || event.metaKey);
              event.currentTarget.focus();
              if (event.ctrlKey || event.metaKey) return;

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
                scrollRef.current.scrollTop = pan.scrollTop - (event.clientY - pan.clientY);
                return;
              }

              if (loopDrag) {
                const currentSample = snapToEdges(sampleAtX(event.clientX));
                setLoopDrag(current => current ? { ...current, currentSample } : current);
                return;
              }

              if (seeking) {
                onSeek(snapSample(sampleAtX(event.clientX)));
                return;
              }

              if (automationDrag) {
                event.currentTarget.style.cursor = 'grabbing';
                updateAutomationDrag(event.clientX, event.clientY);
              } else if (drag) {
                updateDrag(event.clientX, event.clientY);
              } else {
                const hit = locate(event.clientX, event.clientY);
                if (hit?.row.kind === 'automation' && hit.row.lane) {
                  const pointIndex = pointAt(hit.row.lane, hit.row, hit.x, hit.y);
                  event.currentTarget.style.cursor = pointIndex >= 0 ? 'grab' : 'crosshair';
                } else {
                  event.currentTarget.style.cursor = '';
                }
              }
            }}
            onPointerLeave={event => { event.currentTarget.style.cursor = ''; }}
            onPointerUp={() => {
              if (loopDrag) {
                const start = Math.min(loopDrag.startSample, loopDrag.currentSample);
                const end = Math.max(loopDrag.startSample, loopDrag.currentSample);
                onSetLoop(end - start >= sampleRate * 0.01 ? start : null, end - start >= sampleRate * 0.01 ? end : null);
                setLoopDrag(null);
              }
              setSeeking(false);
              endPan();
              finishAutomationDrag();
              finishDrag();
            }}
            onPointerCancel={() => { setLoopDrag(null); setSeeking(false); endPan(); cancelAutomationDrag(); cancelDrag(); }}
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
              event.preventDefault();
              setClipMenu(null);
              setTimelineMenu(null);
              setMarkerMenu(null);
              const canvasRect = event.currentTarget.getBoundingClientRect();
              if (event.clientY - canvasRect.top <= RULER_HEIGHT) {
                const marker = markerAtX(event.clientX);
                if (marker) {
                  setMarkerMenu({ x: event.clientX, y: event.clientY, markerId: marker.id });
                } else {
                  setTimelineMenu({ x: event.clientX, y: event.clientY, sample: sampleAtX(event.clientX) });
                }
                return;
              }

              const hit = locate(event.clientX, event.clientY);
              if (!hit) {
                setTimelineMenu({ x: event.clientX, y: event.clientY, sample: sampleAtX(event.clientX) });
                return;
              }

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
                const key = clipKey(hit.row.track.id, clip.clip.id);
                if (!selectedClipKeys.includes(key)) {
                  setSelectedClipKeys([key]);
                }
                setSelectedClip({ trackId: hit.row.track.id, clipId: clip.clip.id });
                setClipMenu({
                  x: event.clientX,
                  y: event.clientY,
                  trackId: hit.row.track.id,
                  clipId: clip.clip.id,
                });
              } else {
                setTimelineMenu({ x: event.clientX, y: event.clientY, sample: sampleAtX(event.clientX) });
              }
            }}
          />
          <div
            className="timeline-playhead"
            style={{ transform: `translateX(${playheadX}px)`, color: palette.playhead }}
            aria-hidden="true"
          />
          </div>
          </>
        )}
      </div>
      {clipMenu && menuClipModel && (
        <TimelineContextMenu
          x={clipMenu.x}
          y={clipMenu.y}
          ariaLabel={menuClipModel.clip.name || t('Audio clip')}
          onClose={() => setClipMenu(null)}
          menuRef={menuRef}
          items={[
            {
              id: 'split',
              label: t('Split'),
              disabled: !canSplitMenu,
              onClick: () => onSplitClip(menuClipModel.track.id, menuClipModel.clip.id, state.playheadSample),
            },
            {
              id: 'duplicate',
              label: t('Duplicate'),
              onClick: () => onDuplicateClip(menuClipModel.track.id, menuClipModel.clip.id),
            },
            {
              id: 'crossfade',
              label: t('Crossfade'),
              disabled: !canCrossfadeMenu,
              onClick: () => onCrossfadeClip(menuClipModel.track.id, menuClipModel.clip.id),
            },
            {
              id: 'normalize',
              label: t('Normalize'),
              disabled: !canProcessMenu,
              onClick: () => onNormalizeClip(menuClipModel.track.id, menuClipModel.clip.id),
            },
            {
              id: 'reverse',
              label: t('Reverse'),
              disabled: !canProcessMenu,
              onClick: () => onReverseClip(menuClipModel.track.id, menuClipModel.clip.id),
            },
            {
              id: 'consolidate',
              label: t('Consolidate selection'),
              disabled: consolidateClipEntries.length === 0,
              onClick: () => onConsolidateClips(consolidateClipEntries.map(entry => ({
                trackId: entry.trackId,
                clipId: entry.clipId,
              }))),
            },
            {
              id: 'copy',
              label: t('Copy clips'),
              onClick: copySelection,
            },
            {
              id: 'cut',
              label: t('Cut clips'),
              onClick: cutSelection,
            },
            {
              id: 'delete',
              label: t('Delete clips'),
              onClick: removeSelection,
            },
            ...(canPitchMenu ? [{
              id: 'pitch',
              label: `+ ${t('Clip pitch')}`,
              onClick: () => onAddAutomation(menuClipModel.track.id, {
                nodeId: `clip:${menuClipModel.clip.id}`,
                trackId: menuClipModel.track.id,
                label: `${menuClipModel.track.name} \u00B7 ${menuClipModel.clip.name || t('Audio clip')} \u00B7 ${t('Clip pitch')}`,
                processorIndex: -1,
                parameterIndex: 0,
                min: -24,
                max: 24,
                value: menuClipModel.clip.pitchSemitones ?? 0,
              }),
            }] : []),
          ]}
        />
      )}
      {timelineMenu && (
        <TimelineContextMenu
          x={timelineMenu.x}
          y={timelineMenu.y}
          ariaLabel={t('Timeline')}
          onClose={() => setTimelineMenu(null)}
          menuRef={menuRef}
          items={[
            {
              id: 'add-track',
              label: t('Add track'),
              icon: <Plus size={14} />,
              onClick: onAddTrack,
            },
            {
              id: 'add-marker',
              label: t('Add marker here'),
              onClick: () => onAddMarker(timelineMenu.sample),
            },
            {
              id: 'add-region',
              label: t('Add region from loop range'),
              disabled: !loopActive,
              onClick: () => onAddMarker(Math.min(displayLoopStart, displayLoopEnd), Math.max(displayLoopStart, displayLoopEnd)),
            },
            {
              id: 'consolidate-range',
              label: t('Consolidate loop range'),
              disabled: !loopActive,
              onClick: () => onConsolidateRange(Math.min(displayLoopStart, displayLoopEnd), Math.max(displayLoopStart, displayLoopEnd)),
            },
            {
              id: 'paste',
              label: t('Paste clips'),
              disabled: !clipboard,
              onClick: pasteClipboard,
            },
          ]}
        />
      )}
      {markerMenu && (() => {
        const marker = (state.project.markers ?? []).find(item => item.id === markerMenu.markerId);
        if (!marker) return null;
        return (
          <TimelineContextMenu
            x={markerMenu.x}
            y={markerMenu.y}
            ariaLabel={marker.name}
            onClose={() => setMarkerMenu(null)}
            menuRef={menuRef}
            items={[
              {
                id: 'rename-marker',
                label: t('Rename marker'),
                onClick: () => setMarkerRename({ id: marker.id, name: marker.name }),
              },
              {
                id: 'remove-marker',
                label: t('Remove marker'),
                onClick: () => onRemoveMarker(marker.id),
              },
            ]}
          />
        );
      })()}
      {markerRename && (
        <PromptDialog
          title={t('Rename marker')}
          label={t('Marker name')}
          value={markerRename.name}
          onSubmit={name => {
            onUpdateMarker(markerRename.id, name);
            setMarkerRename(null);
          }}
          onClose={() => setMarkerRename(null)}
        />
      )}
      {pendingRemoval && (
        <ConfirmDialog
          title={pendingRemoval.kind === 'track'
            ? t('Remove track')
            : t('Remove this automation lane?')}
          confirmLabel={t('Remove')}
          onConfirm={() => {
            if (pendingRemoval.kind === 'track') onRemoveTrack(pendingRemoval.trackId);
            else onRemoveAutomation(pendingRemoval.trackId, pendingRemoval.laneId);
            setPendingRemoval(null);
          }}
          onClose={() => setPendingRemoval(null)}
        />
      )}
    </section>
  );
}

