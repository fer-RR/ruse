import { useEffect, useRef, useState } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import type { NodeTelemetry } from '../types';
import { useI18n } from '../i18n';

const BEATS_PER_BAR = [2, 3, 4, 6] as const;

export default function MetronomeControls({ node, telemetry }: { node: RuseFlowNode['data']; telemetry?: NodeTelemetry }) {
  const { t } = useI18n();
  const beatActive = (telemetry?.level ?? 0) > 0.02;
  const [bpm, setBpm] = useState(node.metronomeBpm ?? 120);
  const [beats, setBeats] = useState(node.metronomeBeatsPerBar ?? 4);
  const timer = useRef<number | null>(null);
  const pending = useRef<{ bpm: number; beats: number } | null>(null);

  useEffect(() => () => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
      timer.current = null;
      if (pending.current) send('metronome', { id: node.id, ...pending.current });
    }
  }, [node.id]);

  useEffect(() => setBpm(node.metronomeBpm ?? 120), [node.metronomeBpm]);
  useEffect(() => setBeats(node.metronomeBeatsPerBar ?? 4), [node.metronomeBeatsPerBar]);

  const update = (nextBpm: number, nextBeats: number) => {
    pending.current = { bpm: nextBpm, beats: nextBeats };
    if (timer.current !== null) return;
    timer.current = window.setTimeout(() => {
      timer.current = null;
      if (pending.current) {
        send('metronome', { id: node.id, ...pending.current });
        pending.current = null;
      }
    }, 50);
  };

  return (
    <div className="metronome-controls nodrag">
      <div className="metronome-size"><button className="small-button" onClick={() => send('node-size', { id: node.id, width: 240 })}>{t('Compact')}</button><button className="small-button" onClick={() => send('node-size', { id: node.id, width: 320 })}>{t('Wide')}</button></div>
      <div className="metronome-status"><span className={`metronome-beat ${beatActive ? 'active' : ''}`} aria-hidden="true" /><span>{beatActive ? t('Click') : t('Ready')}</span></div>
      <label className="tone-field">
        <span>{t('Tempo')}</span>
        <input
          type="range"
          min={20}
          max={300}
          step={1}
          value={bpm}
          aria-label={t('Tempo')}
          aria-valuetext={`${Math.round(bpm)} BPM`}
          onChange={event => {
            const next = Number(event.target.value);
            setBpm(next);
            update(next, beats);
          }}
        />
        <span>{Math.round(bpm)} BPM</span>
      </label>
      <label className="tone-field">
        <span>{t('Beats per bar')}</span>
        <select
          value={beats}
          onChange={event => {
            const next = Number(event.target.value);
            setBeats(next);
            update(bpm, next);
          }}
        >
          {BEATS_PER_BAR.map(value => <option key={value} value={value}>{value}</option>)}
        </select>
      </label>
    </div>
  );
}
