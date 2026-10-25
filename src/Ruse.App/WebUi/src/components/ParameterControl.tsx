import { useEffect, useState } from 'react';
import type { ParameterModel } from '../types';

export function formatParameter(parameter: ParameterModel, value: number): string {
  if (parameter.isChoice) return parameter.options[Math.round(value)] ?? '';
  const digits = value < 10 && !parameter.isInteger ? 2 : value < 100 && !parameter.isInteger ? 1 : 0;
  return `${value.toFixed(digits)}${parameter.unit ? ` ${parameter.unit}` : ''}`;
}

export default function ParameterControl({ parameter, value, onChange }: {
  parameter: ParameterModel;
  value: number;
  onChange: (value: number) => void;
}) {
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);

  const update = (next: number) => {
    setLocal(next);
    onChange(next);
  };

  if (parameter.isToggle) {
    return (
      <label className="parameter-row toggle-row nodrag">
        <span>{parameter.name}</span>
        <input type="checkbox" checked={local >= 0.5} onChange={event => update(event.target.checked ? 1 : 0)} />
      </label>
    );
  }

  if (parameter.isChoice) {
    return (
      <label className="parameter-row nodrag">
        <span>{parameter.name}</span>
        <select value={Math.round(local)} onChange={event => update(Number(event.target.value))}>
          {parameter.options.map((option, index) => <option key={option} value={index}>{option}</option>)}
        </select>
      </label>
    );
  }

  const logarithmic = parameter.logarithmic && parameter.min > 0;
  const sliderValue = logarithmic
    ? Math.log(local / parameter.min) / Math.log(parameter.max / parameter.min)
    : local;

  return (
    <label className="parameter-stack nodrag">
      <span className="parameter-label"><span>{parameter.name}</span><strong>{formatParameter(parameter, local)}</strong></span>
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
