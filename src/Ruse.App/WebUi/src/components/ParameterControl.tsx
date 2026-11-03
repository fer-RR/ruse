import { useEffect, useState } from 'react';
import type { MouseEvent as ReactMouseEvent } from 'react';
import { send } from '../bridge';
import { useI18n } from '../i18n';
import type { ParameterModel } from '../types';

export function formatParameter(parameter: ParameterModel, value: number): string {
  if (parameter.isChoice) return parameter.options[Math.round(value)] ?? '';
  const digits = value < 10 && !parameter.isInteger ? 2 : value < 100 && !parameter.isInteger ? 1 : 0;
  return `${value.toFixed(digits)}${parameter.unit ? ` ${parameter.unit}` : ''}`;
}

export default function ParameterControl({ nodeId, midiEnabled, parameter, value, onChange }: {
  nodeId?: string;
  midiEnabled?: boolean;
  parameter: ParameterModel;
  value: number;
  onChange: (value: number) => void;
}) {
  const { t } = useI18n();
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);

  const update = (next: number) => {
    setLocal(next);
    onChange(next);
  };

  const midiAction = (event: ReactMouseEvent<HTMLButtonElement>) => {
    if (!nodeId) return;
    event.preventDefault();
    event.stopPropagation();
    send(event.type === 'contextmenu' ? 'midi-unbind' : 'midi-learn', {
      id: nodeId,
      processor: parameter.processorIndex,
      parameter: parameter.parameterIndex,
    });
  };

  const midiButton = !nodeId || !midiEnabled ? null : (
    <button
      type="button"
      className={`param-midi ${parameter.midiBinding ? 'bound' : ''} ${parameter.midiLearning ? 'learning' : ''}`}
      title={parameter.midiBinding
        ? t('MIDI: {binding}. Click to relearn, right-click to clear.', { binding: parameter.midiBinding })
        : t('Learn MIDI')}
      aria-label={parameter.midiBinding ? t('MIDI: {binding}. Click to relearn, right-click to clear.', { binding: parameter.midiBinding }) : t('Learn MIDI')}
      onClick={midiAction}
      onContextMenu={midiAction}
    >
      MIDI
    </button>
  );

  if (parameter.isToggle) {
    return (
      <label className="parameter-row toggle-row nodrag">
        <span>{parameter.name}</span>
        <span className="parameter-row-actions">
          {midiButton}
          <input type="checkbox" checked={local >= 0.5} onChange={event => update(event.target.checked ? 1 : 0)} />
        </span>
      </label>
    );
  }

  if (parameter.isChoice) {
    return (
      <label className="parameter-row nodrag">
        <span>{parameter.name}</span>
        <span className="parameter-row-actions">
          {midiButton}
          <select value={Math.round(local)} onChange={event => update(Number(event.target.value))}>
            {parameter.options.map((option, index) => <option key={option} value={index}>{option}</option>)}
          </select>
        </span>
      </label>
    );
  }

  const logarithmic = parameter.logarithmic && parameter.min > 0;
  const sliderValue = logarithmic
    ? Math.log(local / parameter.min) / Math.log(parameter.max / parameter.min)
    : local;

  return (
    <label className="parameter-stack nodrag">
      <span className="parameter-label">
        <span>{parameter.name}</span>
        <span className="parameter-label-actions">
          {midiButton}
          <strong>{formatParameter(parameter, local)}</strong>
        </span>
      </span>
      <input
        type="range"
        min={logarithmic ? 0 : parameter.min}
        max={logarithmic ? 1 : parameter.max}
        step={logarithmic ? 0.001 : parameter.isInteger ? 1 : (parameter.max - parameter.min) / 1000}
        value={sliderValue}
        onChange={event => {
          const raw = Number(event.target.value);
          const next = logarithmic ? parameter.min * Math.pow(parameter.max / parameter.min, raw) : raw;
          update(parameter.isInteger ? Math.round(next) : next);
        }}
      />
    </label>
  );
}
