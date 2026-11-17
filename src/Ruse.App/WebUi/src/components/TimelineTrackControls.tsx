import { useEffect, useRef, useState } from 'react';
import type { TimelineTrackModel } from '../timeline';
import { formatGainDb, nextVolumeValue } from '../gain';
import { useNodeTelemetry } from '../telemetry';

export function TrackMeter({ nodeId }: { nodeId?: string }) {
  const telemetry = useNodeTelemetry(nodeId ?? '');
  const level = Math.max(0, Math.min(1, telemetry?.level ?? 0));
  const [peak, setPeak] = useState(level);
  useEffect(() => {
    setPeak(current => Math.max(level, current));
    const timer = window.setTimeout(() => {
      setPeak(current => current > level + 0.01 ? current * 0.86 : level);
    }, 650);
    return () => window.clearTimeout(timer);
  }, [level]);
  return (
    <span className={`timeline-track-meter ${telemetry?.clipping ? 'clipping' : ''}`} aria-hidden="true">
      <span style={{ width: `${level * 100}%` }} />
      <i style={{ left: `${Math.min(100, peak * 100)}%` }} />
    </span>
  );
}

export function TrackGainSlider({ track, onChange }: { track: TimelineTrackModel; onChange: (gain: number) => void }) {
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
