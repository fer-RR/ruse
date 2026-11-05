import { useEffect, useRef } from 'react';
import { useI18n } from '../i18n';

const WIDTH = 428;
const HEIGHT = 200;
const TOP = 5;
const BOTTOM = HEIGHT - 12;
const DEFAULT_BANDS = 128;
const FREQUENCY_REFS = [
  [50, '50'], [100, '100'], [250, '250'], [500, '500'], [1000, '1k'],
  [2000, '2k'], [5000, '5k'], [10000, '10k'], [20000, '20k'],
] as const;

function formatHz(value: number | undefined): string {
  if (!value || !Number.isFinite(value)) return '';
  return value >= 1000 ? `${(value / 1000).toFixed(1)} kHz` : `${Math.round(value)} Hz`;
}

function dbToY(value: number, floor: number, ceiling: number): number {
  const span = Math.max(1, ceiling - floor);
  const normalized = Math.max(0, Math.min(1, (value - floor) / span));
  return BOTTOM - normalized * (BOTTOM - TOP);
}

function buildStatic(floor: number, ceiling: number): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = WIDTH;
  canvas.height = HEIGHT;
  const context = canvas.getContext('2d');
  if (!context) return canvas;

  context.strokeStyle = 'rgba(255,255,255,0.07)';
  context.lineWidth = 1;
  for (const db of [-60, -40, -20, 0]) {
    const y = dbToY(db, floor, ceiling);
    if (y < TOP || y > BOTTOM) continue;
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(WIDTH, y);
    context.stroke();
  }

  context.font = '9px Consolas, monospace';
  context.textBaseline = 'bottom';
  for (const [frequency, label] of FREQUENCY_REFS) {
    const x = (Math.log(frequency / 20) / Math.log(1000)) * WIDTH;
    context.strokeStyle = 'rgba(255,255,255,0.055)';
    context.beginPath();
    context.moveTo(x, TOP);
    context.lineTo(x, BOTTOM);
    context.stroke();
    context.fillStyle = 'rgba(255,255,255,0.38)';
    context.fillText(label, Math.max(1, Math.min(WIDTH - 18, x)), HEIGHT - 1);
  }

  return canvas;
}

export default function SpectrumView({
  bands,
  peakHz,
  color,
  floor = -80,
  ceiling = 0,
}: {
  bands?: number[];
  peakHz?: number;
  color: string;
  floor?: number;
  ceiling?: number;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const targetsRef = useRef<number[]>([]);
  const displayRef = useRef<number[]>([]);
  const peaksRef = useRef<number[]>([]);
  const configRef = useRef({ color, floor, ceiling, t, hasBands: false });
  const staticRef = useRef<HTMLCanvasElement | null>(null);
  const staticKeyRef = useRef('');
  const gradientRef = useRef<{ key: string; gradient: CanvasGradient } | null>(null);

  useEffect(() => {
    const target = bands?.length ? bands.slice() : new Array(DEFAULT_BANDS).fill(floor);
    targetsRef.current = target;
    configRef.current.hasBands = Boolean(bands?.length);
    if (displayRef.current.length !== target.length) {
      displayRef.current = target.slice();
      peaksRef.current = target.slice();
    }
  }, [bands, floor]);

  useEffect(() => {
    configRef.current.color = color;
    configRef.current.floor = floor;
    configRef.current.ceiling = ceiling;
    configRef.current.t = t;
    gradientRef.current = null;
  }, [color, floor, ceiling, t]);

  useEffect(() => {
    let frame = 0;
    let last = 0;
    const draw = (time: number) => {
      if (time - last >= 33) {
        last = time;
        const canvas = ref.current;
        const context = canvas?.getContext('2d');
        if (context) {
          const config = configRef.current;
          const target = targetsRef.current;
          const display = displayRef.current;
          const peaks = peaksRef.current;
          if (display.length !== target.length) {
            display.length = 0;
            display.push(...target);
            peaks.length = 0;
            peaks.push(...target);
          }

          for (let index = 0; index < display.length; index++) {
            const difference = target[index] - display[index];
            display[index] = Math.abs(difference) < 0.04
              ? target[index]
              : display[index] + difference * 0.24;
            peaks[index] = Math.max(display[index], peaks[index] - 0.45);
          }

          const staticKey = `${config.floor}:${config.ceiling}`;
          if (!staticRef.current || staticKeyRef.current !== staticKey) {
            staticRef.current = buildStatic(config.floor, config.ceiling);
            staticKeyRef.current = staticKey;
          }

          if (!gradientRef.current || gradientRef.current.key !== config.color) {
            const gradient = context.createLinearGradient(0, BOTTOM, 0, TOP);
            gradient.addColorStop(0, config.color);
            gradient.addColorStop(0.68, '#65D6D0');
            gradient.addColorStop(1, '#E5AD51');
            gradientRef.current = { key: config.color, gradient };
          }

          context.clearRect(0, 0, WIDTH, HEIGHT);

          const count = display.length;
          const barWidth = WIDTH / Math.max(1, count);
          context.beginPath();
          context.moveTo(0, BOTTOM);
          for (let index = 0; index < count; index++) {
            const x = index * barWidth + barWidth / 2;
            context.lineTo(x, dbToY(display[index], config.floor, config.ceiling));
          }

          context.lineTo(WIDTH, BOTTOM);
          context.closePath();
          context.fillStyle = gradientRef.current!.gradient;
          context.globalAlpha = 0.24;
          context.fill();
          context.globalAlpha = 1;

          context.beginPath();
          for (let index = 0; index < count; index++) {
            const x = index * barWidth + barWidth / 2;
            const y = dbToY(display[index], config.floor, config.ceiling);
            if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
          }

          context.strokeStyle = config.color;
          context.lineWidth = 1.4;
          context.stroke();

          context.beginPath();
          for (let index = 0; index < peaks.length; index++) {
            const x = index * barWidth + barWidth / 2;
            const y = dbToY(peaks[index], config.floor, config.ceiling);
            if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
          }

          context.strokeStyle = 'rgba(255,255,255,0.42)';
          context.lineWidth = 0.8;
          context.stroke();

          if (staticRef.current) {
            context.drawImage(staticRef.current, 0, 0);
          }

          if (!config.hasBands) {
            context.fillStyle = 'rgba(255,255,255,0.22)';
            context.font = '8px system-ui, sans-serif';
            context.fillText(config.t('Waiting for signal'), 6, BOTTOM - 6);
          }
        }
      }

      frame = requestAnimationFrame(draw);
    };

    frame = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(frame);
  }, []);

  return (
    <div className="spectrum-view">
      <canvas ref={ref} width={WIDTH} height={HEIGHT} aria-label={t('Live spectrum')} />
      <div className="spectrum-meta">
        <span>{t('20 Hz - 20 kHz')}</span>
        <strong>{formatHz(peakHz)}</strong>
      </div>
    </div>
  );
}
