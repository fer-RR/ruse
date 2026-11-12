import { useEffect, useRef, useState } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

const TONE_WAVEFORMS = [
  ['Sine', 'Sine'],
  ['Square', 'Square'],
  ['Saw', 'Saw'],
  ['Triangle', 'Triangle'],
  ['Noise', 'Noise'],
  ['PinkNoise', 'Pink noise'],
  ['BrownNoise', 'Brown noise'],
] as const;
const TONE_MIN_HZ = 20;
const TONE_MAX_HZ = 20000;

function frequencyToSlider(frequency: number): number {
  const clamped = Math.max(TONE_MIN_HZ, Math.min(TONE_MAX_HZ, frequency));
  return 1000 * Math.log(clamped / TONE_MIN_HZ) / Math.log(TONE_MAX_HZ / TONE_MIN_HZ);
}

function sliderToFrequency(value: number): number {
  return TONE_MIN_HZ * Math.pow(TONE_MAX_HZ / TONE_MIN_HZ, value / 1000);
}

export default function ToneControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [frequency, setFrequency] = useState(node.toneFrequency ?? 1000);
  const [waveform, setWaveform] = useState(node.toneWaveform ?? 'Sine');
  const [level, setLevel] = useState(node.toneLevelDb ?? -18);
  const [interacting, setInteracting] = useState(false);
  const interactionTimer = useRef<number | null>(null);
  const pending = useRef<{ frequency: number; waveform: string; level: number } | null>(null);
  const flushTimer = useRef<number | null>(null);

  useEffect(() => () => {
    if (flushTimer.current !== null) {
      window.clearTimeout(flushTimer.current);
      flushTimer.current = null;
      if (pending.current) {
        send('tone', { id: node.id, ...pending.current });
        pending.current = null;
      }
    }

    if (interactionTimer.current !== null) {
      window.clearTimeout(interactionTimer.current);
      interactionTimer.current = null;
    }
  }, [node.id]);

  useEffect(() => {
    if (!interacting) return;
    const release = () => endInteraction();
    window.addEventListener('pointerup', release);
    window.addEventListener('pointercancel', release);
    return () => {
      window.removeEventListener('pointerup', release);
      window.removeEventListener('pointercancel', release);
    };
  }, [interacting]);

  useEffect(() => {
    if (!interacting) setFrequency(node.toneFrequency ?? 1000);
  }, [node.toneFrequency, interacting]);
  useEffect(() => {
    if (!interacting) setWaveform(node.toneWaveform ?? 'Sine');
  }, [node.toneWaveform, interacting]);
  useEffect(() => {
    if (!interacting) setLevel(node.toneLevelDb ?? -18);
  }, [node.toneLevelDb, interacting]);

  const beginInteraction = () => {
    if (interactionTimer.current !== null) window.clearTimeout(interactionTimer.current);
    setInteracting(true);
  };

  const endInteraction = () => {
    if (interactionTimer.current !== null) window.clearTimeout(interactionTimer.current);
    interactionTimer.current = window.setTimeout(() => {
      interactionTimer.current = null;
      setInteracting(false);
    }, 180);
  };

  const update = (nextFrequency = frequency, nextWaveform = waveform, nextLevel = level) => {
    pending.current = { frequency: nextFrequency, waveform: nextWaveform, level: nextLevel };
    if (flushTimer.current === null) {
      flushTimer.current = window.setTimeout(() => {
        flushTimer.current = null;
        if (pending.current) {
        send('tone', { id: node.id, ...pending.current });
        pending.current = null;
      }
      }, 50);
    }
  };

  return (
    <div className="tone-controls nodrag" onPointerDown={beginInteraction} onPointerUp={endInteraction} onPointerCancel={endInteraction}>
      <label className="tone-field">
        <span>{t('Waveform')}</span>
        <select value={waveform} onChange={event => { const next = event.target.value; setWaveform(next); update(frequency, next, level); }}>
          {TONE_WAVEFORMS.map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}
        </select>
      </label>
      <label className="tone-field">
        <span>{t('Frequency')}</span>
        <input
          type="range"
          min={0}
          max={1000}
          step={1}
          value={frequencyToSlider(frequency)}
          onChange={event => {
            const next = Math.round(sliderToFrequency(Number(event.target.value)));
            setFrequency(next);
            update(next, waveform, level);
          }}
        />
        <span>{Math.round(frequency)} Hz</span>
      </label>
      <label className="tone-field">
        <span>{t('Level')}</span>
        <input
          type="range"
          min={-60}
          max={0}
          step={1}
          value={level}
          onChange={event => {
            const next = Number(event.target.value);
            setLevel(next);
            update(frequency, waveform, next);
          }}
        />
        <span>{Math.round(level)} dB</span>
      </label>
    </div>
  );
}
