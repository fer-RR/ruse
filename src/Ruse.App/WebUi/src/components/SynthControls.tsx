import { useEffect, useRef, useState } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

const WAVEFORMS = [
  ['Sine', 'Sine'],
  ['Square', 'Square'],
  ['Saw', 'Saw'],
  ['Triangle', 'Triangle'],
  ['Noise', 'Noise'],
  ['PinkNoise', 'Pink noise'],
  ['BrownNoise', 'Brown noise'],
] as const;

interface SynthState {
  frequency: number;
  waveform: string;
  level: number;
  harmonics: number;
  detune: number;
  noise: number;
  cutoff: number;
  resonance: number;
  movement: number;
  lfoRate: number;
  width: number;
}

function fromNode(node: RuseFlowNode['data']): SynthState {
  return {
    frequency: node.synthFrequency ?? 110,
    waveform: node.synthWaveform ?? 'Saw',
    level: node.synthLevelDb ?? -18,
    harmonics: node.synthHarmonics ?? 0.3,
    detune: node.synthDetuneCents ?? 6,
    noise: node.synthNoise ?? 0.05,
    cutoff: node.synthCutoffHz ?? 2200,
    resonance: node.synthResonance ?? 0.2,
    movement: node.synthMovement ?? 0.15,
    lfoRate: node.synthLfoRateHz ?? 0.7,
    width: node.synthStereoWidth ?? 0.4,
  };
}

function logSlider(value: number, min: number, max: number): number {
  const clamped = Math.max(min, Math.min(max, value));
  return 1000 * Math.log(clamped / min) / Math.log(max / min);
}

function fromLogSlider(value: number, min: number, max: number): number {
  return min * Math.pow(max / min, value / 1000);
}

export default function SynthControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [settings, setSettings] = useState<SynthState>(() => fromNode(node));
  const [interacting, setInteracting] = useState(false);
  const interactionTimer = useRef<number | null>(null);
  const settingsRef = useRef(settings);
  const pending = useRef<SynthState | null>(null);
  const flushTimer = useRef<number | null>(null);
  const commitTimer = useRef<number | null>(null);

  useEffect(() => {
    settingsRef.current = settings;
  }, [settings]);

  useEffect(() => () => {
    if (flushTimer.current !== null) {
      window.clearTimeout(flushTimer.current);
      flushTimer.current = null;
      if (pending.current) {
        send('synth', { id: node.id, ...pending.current, commit: true });
        pending.current = null;
      }
    }

    if (interactionTimer.current !== null) {
      window.clearTimeout(interactionTimer.current);
      interactionTimer.current = null;
    }

    if (commitTimer.current !== null) {
      window.clearTimeout(commitTimer.current);
      commitTimer.current = null;
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
    if (!interacting) setSettings(fromNode(node));
  }, [
    node.synthFrequency,
    node.synthWaveform,
    node.synthLevelDb,
    node.synthHarmonics,
    node.synthDetuneCents,
    node.synthNoise,
    node.synthCutoffHz,
    node.synthResonance,
    node.synthMovement,
    node.synthLfoRateHz,
    node.synthStereoWidth,
    interacting,
  ]);

  const beginInteraction = () => {
    if (interactionTimer.current !== null) window.clearTimeout(interactionTimer.current);
    setInteracting(true);
  };

  const commit = () => {
    if (commitTimer.current !== null) {
      window.clearTimeout(commitTimer.current);
      commitTimer.current = null;
    }

    const value = pending.current ?? settingsRef.current;
    send('synth', { id: node.id, ...value, commit: true });
    pending.current = null;
  };

  const endInteraction = () => {
    if (interactionTimer.current !== null) window.clearTimeout(interactionTimer.current);
    interactionTimer.current = window.setTimeout(() => {
      interactionTimer.current = null;
      setInteracting(false);
    }, 180);
    commit();
  };

  const update = (patch: Partial<SynthState>) => {
    const next = { ...settingsRef.current, ...patch };
    settingsRef.current = next;
    pending.current = next;
    setSettings(next);
    if (flushTimer.current === null) {
      flushTimer.current = window.setTimeout(() => {
        flushTimer.current = null;
        if (pending.current) {
          send('synth', { id: node.id, ...pending.current, commit: false });
          pending.current = null;
        }
      }, 50);

    }

    if (commitTimer.current !== null) window.clearTimeout(commitTimer.current);
    commitTimer.current = window.setTimeout(commit, 300);
  };

  return (
    <div className="synth-controls nodrag" onPointerDown={beginInteraction} onPointerUp={endInteraction} onPointerCancel={endInteraction}>
      <label className="synth-field">
        <span>{t('Timbre')}</span>
        <select
          value={settings.waveform}
          onChange={event => update({ waveform: event.target.value })}
        >
          {WAVEFORMS.map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}
        </select>
      </label>
      <label className="synth-field">
        <span>{t('Pitch')}</span>
        <input
          type="range"
          min={0}
          max={1000}
          step={1}
          value={logSlider(settings.frequency, 20, 4000)}
          onChange={event => update({ frequency: Math.round(fromLogSlider(Number(event.target.value), 20, 4000)) })}
        />
        <span>{Math.round(settings.frequency)} Hz</span>
      </label>
      <label className="synth-field">
        <span>{t('Level')}</span>
        <input
          type="range"
          min={-60}
          max={0}
          step={1}
          value={settings.level}
          onChange={event => update({ level: Number(event.target.value) })}
        />
        <span>{Math.round(settings.level)} dB</span>
      </label>
      <label className="synth-field">
        <span>{t('Harmonics')}</span>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          value={settings.harmonics}
          onChange={event => update({ harmonics: Number(event.target.value) })}
        />
        <span>{Math.round(settings.harmonics * 100)}%</span>
      </label>
      <label className="synth-field">
        <span>{t('Detune')}</span>
        <input
          type="range"
          min={0}
          max={50}
          step={1}
          value={settings.detune}
          onChange={event => update({ detune: Number(event.target.value) })}
        />
        <span>{Math.round(settings.detune)} ct</span>
      </label>
      <label className="synth-field">
        <span>{t('Noise')}</span>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          value={settings.noise}
          onChange={event => update({ noise: Number(event.target.value) })}
        />
        <span>{Math.round(settings.noise * 100)}%</span>
      </label>
      <div className="synth-divider" />
      <label className="synth-field">
        <span>{t('Cutoff')}</span>
        <input
          type="range"
          min={0}
          max={1000}
          step={1}
          value={logSlider(settings.cutoff, 60, 14000)}
          onChange={event => update({ cutoff: Math.round(fromLogSlider(Number(event.target.value), 60, 14000)) })}
        />
        <span>{settings.cutoff >= 1000 ? `${(settings.cutoff / 1000).toFixed(1)} kHz` : `${Math.round(settings.cutoff)} Hz`}</span>
      </label>
      <label className="synth-field">
        <span>{t('Resonance')}</span>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          value={settings.resonance}
          onChange={event => update({ resonance: Number(event.target.value) })}
        />
        <span>{Math.round(settings.resonance * 100)}%</span>
      </label>
      <div className="synth-divider" />
      <label className="synth-field">
        <span>{t('Movement')}</span>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          value={settings.movement}
          onChange={event => update({ movement: Number(event.target.value) })}
        />
        <span>{Math.round(settings.movement * 100)}%</span>
      </label>
      <label className="synth-field">
        <span>{t('Motion rate')}</span>
        <input
          type="range"
          min={0.05}
          max={12}
          step={0.05}
          value={settings.lfoRate}
          onChange={event => update({ lfoRate: Number(event.target.value) })}
        />
        <span>{settings.lfoRate.toFixed(2)} Hz</span>
      </label>
      <label className="synth-field">
        <span>{t('Stereo width')}</span>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          value={settings.width}
          onChange={event => update({ width: Number(event.target.value) })}
        />
        <span>{Math.round(settings.width * 100)}%</span>
      </label>
    </div>
  );
}
