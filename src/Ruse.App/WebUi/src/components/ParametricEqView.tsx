import { useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent as ReactKeyboardEvent, PointerEvent as ReactPointerEvent } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';
import type { ParameterModel } from '../types';
import ParameterControl from './ParameterControl';

const F_MIN = 20;
const F_MAX = 20000;
const G_MIN = -24;
const G_MAX = 24;
const SAMPLE_RATE = 48000;
const DEFAULT_TYPES = ['Low shelf', 'Peak', 'Peak', 'Peak', 'Peak', 'High shelf'];

interface EqBand {
  index: number;
  type: string;
  frequency: number;
  gain: number;
  q: number;
  frequencyParameter?: ParameterModel;
  gainParameter?: ParameterModel;
  qParameter?: ParameterModel;
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

function frequencyToX(frequency: number, width: number): number {
  const ratio = Math.log(clamp(frequency, F_MIN, F_MAX) / F_MIN) / Math.log(F_MAX / F_MIN);
  return ratio * width;
}

function xToFrequency(x: number, width: number): number {
  return F_MIN * Math.pow(F_MAX / F_MIN, clamp(x / Math.max(1, width), 0, 1));
}

function gainToY(gain: number, height: number): number {
  return ((G_MAX - clamp(gain, G_MIN, G_MAX)) / (G_MAX - G_MIN)) * height;
}

function yToGain(y: number, height: number): number {
  return G_MAX - clamp(y / Math.max(1, height), 0, 1) * (G_MAX - G_MIN);
}

function buildBands(node: RuseFlowNode['data']): EqBand[] {
  const types = node.eqBandTypes?.length ? node.eqBandTypes : DEFAULT_TYPES;
  return types.map((type, index) => {
    const prefix = `Band ${index + 1}`;
    const frequencyParameter = node.parameters.find(parameter => parameter.name === `${prefix} Frequency`);
    const gainParameter = node.parameters.find(parameter => parameter.name === `${prefix} Gain`);
    const qParameter = node.parameters.find(parameter => parameter.name === `${prefix} Q`);
    return {
      index,
      type,
      frequency: frequencyParameter?.value ?? [80, 250, 1000, 3000, 8000, 12000][index] ?? 1000,
      gain: gainParameter?.value ?? 0,
      q: qParameter?.value ?? 0.7,
      frequencyParameter,
      gainParameter,
      qParameter,
    };
  });
}

interface Coefficients {
  b0: number;
  b1: number;
  b2: number;
  a1: number;
  a2: number;
}

function bandCoefficients(band: EqBand): Coefficients {
  const w0 = (2 * Math.PI * clamp(band.frequency, F_MIN, F_MAX)) / SAMPLE_RATE;
  const cos = Math.cos(w0);
  const sin = Math.sin(w0);
  const amp = Math.pow(10, band.gain / 40);
  let b0: number;
  let b1: number;
  let b2: number;
  let a0: number;
  let a1: number;
  let a2: number;

  if (band.type === 'Low shelf') {
    const alpha = (sin / 2) * Math.SQRT2;
    const beta = 2 * Math.sqrt(amp) * alpha;
    a0 = (amp + 1) + ((amp - 1) * cos) + beta;
    b0 = amp * ((amp + 1) - ((amp - 1) * cos) + beta);
    b1 = 2 * amp * ((amp - 1) - ((amp + 1) * cos));
    b2 = amp * ((amp + 1) - ((amp - 1) * cos) - beta);
    a1 = -2 * ((amp - 1) + ((amp + 1) * cos));
    a2 = (amp + 1) + ((amp - 1) * cos) - beta;
  } else if (band.type === 'High shelf') {
    const alpha = (sin / 2) * Math.SQRT2;
    const beta = 2 * Math.sqrt(amp) * alpha;
    a0 = (amp + 1) - ((amp - 1) * cos) + beta;
    b0 = amp * ((amp + 1) + ((amp - 1) * cos) + beta);
    b1 = -2 * amp * ((amp - 1) + ((amp + 1) * cos));
    b2 = amp * ((amp + 1) + ((amp - 1) * cos) - beta);
    a1 = 2 * ((amp - 1) - ((amp + 1) * cos));
    a2 = (amp + 1) - ((amp - 1) * cos) - beta;
  } else {
    const alpha = sin / (2 * Math.max(0.1, band.q));
    a0 = 1 + alpha / amp;
    b0 = 1 + alpha * amp;
    b1 = -2 * cos;
    b2 = 1 - alpha * amp;
    a1 = -2 * cos;
    a2 = 1 - alpha / amp;
  }

  return {
    b0: b0 / a0,
    b1: b1 / a0,
    b2: b2 / a0,
    a1: a1 / a0,
    a2: a2 / a0,
  };
}

function responseDb(bands: EqBand[], frequency: number): number {
  const w = (2 * Math.PI * frequency) / SAMPLE_RATE;
  const cos1 = Math.cos(w);
  const sin1 = Math.sin(w);
  const cos2 = Math.cos(2 * w);
  const sin2 = Math.sin(2 * w);
  let total = 0;

  for (const band of bands) {
    if (band.gain === 0 && band.type !== 'Low shelf' && band.type !== 'High shelf') {
      continue;
    }

    const c = bandCoefficients(band);
    const numReal = c.b0 + (c.b1 * cos1) + (c.b2 * cos2);
    const numImag = -((c.b1 * sin1) + (c.b2 * sin2));
    const denReal = 1 + (c.a1 * cos1) + (c.a2 * cos2);
    const denImag = -((c.a1 * sin1) + (c.a2 * sin2));
    const magnitude = Math.hypot(numReal, numImag) / Math.max(1e-9, Math.hypot(denReal, denImag));
    total += 20 * Math.log10(Math.max(1e-6, magnitude));
  }

  return total;
}

function ParametricEqView({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [bands, setBands] = useState<EqBand[]>(() => buildBands(node));
  const [selected, setSelected] = useState(1);
  const draggingRef = useRef<number | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const mixParameter = node.parameters.find(parameter => parameter.name === 'Mix');
  const [mix, setMix] = useState(mixParameter?.value ?? 1);

  useEffect(() => {
    if (draggingRef.current === null) {
      setBands(buildBands(node));
    }
  }, [node.eqBandTypes, node.parameters]);

  useEffect(() => {
    setMix(mixParameter?.value ?? 1);
  }, [mixParameter?.value]);

  const curve = useMemo(() => {
    const points = 180;
    return Array.from({ length: points + 1 }, (_, index) => {
      const frequency = F_MIN * Math.pow(F_MAX / F_MIN, index / points);
      return { frequency, gain: clamp(responseDb(bands, frequency), G_MIN - 6, G_MAX + 6) };
    });
  }, [bands]);

  const selectedBand = bands[Math.min(selected, bands.length - 1)];

  const sendParameter = (parameter: ParameterModel | undefined, value: number) => {
    if (!parameter) return;
    send('param', {
      id: node.id,
      processor: parameter.processorIndex,
      parameter: parameter.parameterIndex,
      value,
    });
  };

  const updateBand = (index: number, frequency: number, gain: number) => {
    setBands(current => current.map(band => band.index === index ? { ...band, frequency, gain } : band));
    const band = bands[index];
    sendParameter(band.frequencyParameter, frequency);
    sendParameter(band.gainParameter, gain);
  };

  const endDrag = () => {
    draggingRef.current = null;
  };

  const handlePointer = (event: ReactPointerEvent<SVGSVGElement>) => {
    const band = draggingRef.current;
    if (band === null || !svgRef.current) return;
    const rect = svgRef.current.getBoundingClientRect();
    const x = ((event.clientX - rect.left) / rect.width) * 260;
    const y = ((event.clientY - rect.top) / rect.height) * 140;
    updateBand(band, Math.round(xToFrequency(x, 260)), Math.round(yToGain(y, 120) * 10) / 10);
  };

  const handleHandleKey = (event: ReactKeyboardEvent<SVGCircleElement>, band: EqBand) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    event.preventDefault();
    const step = event.shiftKey ? 10 : 1;
    const frequency = event.key === 'ArrowLeft'
      ? band.frequency / Math.pow(1.01, step)
      : event.key === 'ArrowRight'
        ? band.frequency * Math.pow(1.01, step)
        : band.frequency;
    const gain = event.key === 'ArrowUp'
      ? band.gain + 0.5 * step
      : event.key === 'ArrowDown'
        ? band.gain - 0.5 * step
        : band.gain;
    updateBand(band.index, Math.round(clamp(frequency, F_MIN, F_MAX)), clamp(gain, G_MIN, G_MAX));
  };

  const setBandValue = (band: EqBand, key: 'frequency' | 'gain' | 'q', value: number) => {
    setBands(current => current.map(item => item.index === band.index ? { ...item, [key]: value } : item));
    const parameter = key === 'frequency' ? band.frequencyParameter
      : key === 'gain' ? band.gainParameter
        : band.qParameter;
    sendParameter(parameter, value);
  };

  return (
    <div className="eq-view nodrag">
      <svg
        ref={svgRef}
        className="eq-canvas"
        viewBox="0 0 260 140"
        onPointerMove={handlePointer}
        onPointerUp={endDrag}
        onPointerCancel={endDrag}
      >
        {[-24, -12, 0, 12, 24].map(gain => (
          <g key={gain}>
            <line x1={0} x2={260} y1={gainToY(gain, 120)} y2={gainToY(gain, 120)} className={gain === 0 ? 'eq-zero-line' : 'eq-grid-line'} />
            <text x={2} y={gainToY(gain, 120) - 2} className="eq-grid-label">{gain}</text>
          </g>
        ))}
        {[100, 1000, 10000].map(frequency => (
          <g key={frequency}>
            <line x1={frequencyToX(frequency, 260)} x2={frequencyToX(frequency, 260)} y1={0} y2={120} className="eq-grid-line" />
            <text x={frequencyToX(frequency, 260)} y={134} className="eq-grid-label" textAnchor="middle">
              {frequency >= 1000 ? `${frequency / 1000}k` : frequency}
            </text>
          </g>
        ))}
        <path
          className="eq-curve"
          d={curve.map((point, index) => `${index === 0 ? 'M' : 'L'}${frequencyToX(point.frequency, 260).toFixed(2)},${gainToY(point.gain, 120).toFixed(2)}`).join(' ')}
        />
        {bands.map(band => (
          <circle
            key={band.index}
            cx={frequencyToX(band.frequency, 260)}
            cy={gainToY(band.gain, 120)}
            r={band.index === selected ? 5.5 : 4}
            className={`eq-handle ${band.index === selected ? 'selected' : ''}`}
            tabIndex={0}
            role="slider"
            aria-label={`${t(band.type)} ${band.index + 1}`}
            aria-valuemin={F_MIN}
            aria-valuemax={F_MAX}
            aria-valuenow={Math.round(band.frequency)}
            aria-valuetext={`${Math.round(band.frequency)} Hz, ${band.gain.toFixed(1)} dB`}
            onKeyDown={event => handleHandleKey(event, band)}
            onPointerDown={event => {
              event.stopPropagation();
              setSelected(band.index);
              draggingRef.current = band.index;
              svgRef.current?.setPointerCapture(event.pointerId);
            }}
          />
        ))}
      </svg>

      <div className="eq-band-row">
        {bands.map(band => (
          <button
            key={band.index}
            type="button"
            className={`eq-band-chip ${band.index === selected ? 'active' : ''}`}
            onClick={() => setSelected(band.index)}
            title={t(band.type)}
          >
            {band.index + 1}
          </button>
        ))}
        <span className="eq-band-type">{t(selectedBand?.type ?? 'Peak')}</span>
      </div>

      {selectedBand?.qParameter && !node.midiEnabled && (
        <label className="eq-q-row">
          <span>Q</span>
          <input
            type="range"
            min={0.1}
            max={10}
            step={0.01}
            value={selectedBand.q}
            onChange={event => setBandValue(selectedBand, 'q', Number(event.target.value))}
          />
          <span>{selectedBand.q.toFixed(2)}</span>
        </label>
      )}

      {node.midiEnabled && selectedBand && (
        <div className="eq-midi-params">
          {selectedBand.frequencyParameter && (
            <ParameterControl
              nodeId={node.id}
              midiEnabled
              parameter={selectedBand.frequencyParameter}
              value={selectedBand.frequency}
              onChange={value => setBandValue(selectedBand, 'frequency', value)}
            />
          )}
          {selectedBand.gainParameter && (
            <ParameterControl
              nodeId={node.id}
              midiEnabled
              parameter={selectedBand.gainParameter}
              value={selectedBand.gain}
              onChange={value => setBandValue(selectedBand, 'gain', value)}
            />
          )}
          {selectedBand.qParameter && (
            <ParameterControl
              nodeId={node.id}
              midiEnabled
              parameter={selectedBand.qParameter}
              value={selectedBand.q}
              onChange={value => setBandValue(selectedBand, 'q', value)}
            />
          )}
        </div>
      )}

      {mixParameter && (
        <label className="eq-mix-row">
          <span>{t('Mix')}</span>
          <input
            type="range"
            min={0}
            max={1}
            step={0.01}
            value={mix}
            onChange={event => {
              const value = Number(event.target.value);
              setMix(value);
              sendParameter(mixParameter, value);
            }}
          />
          <span>{Math.round(mix * 100)}%</span>
        </label>
      )}
    </div>
  );
}

export default ParametricEqView;
