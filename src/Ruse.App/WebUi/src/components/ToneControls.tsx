import { useEffect, useState } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

const TONE_WAVEFORMS = ['Sine', 'Square', 'Saw', 'Triangle', 'Noise'] as const;
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
  useEffect(() => setFrequency(node.toneFrequency ?? 1000), [node.toneFrequency]);
  useEffect(() => setWaveform(node.toneWaveform ?? 'Sine'), [node.toneWaveform]);
  useEffect(() => setLevel(node.toneLevelDb ?? -18), [node.toneLevelDb]);

  const update = (nextFrequency = frequency, nextWaveform = waveform, nextLevel = level) =>
    send('tone', { id: node.id, frequency: nextFrequency, waveform: nextWaveform, level: nextLevel });

  return (
    <div className="tone-controls nodrag">
      <label className="tone-field">
        <span>{t('Waveform')}</span>
        <select value={waveform} onChange={event => { const next = event.target.value; setWaveform(next); update(frequency, next, level); }}>
          {TONE_WAVEFORMS.map(wave => <option key={wave} value={wave}>{t(wave)}</option>)}
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
