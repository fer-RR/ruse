import { useEffect, useRef } from 'react';
import { useElementVisible } from '../hooks/useElementVisible';
import { useI18n } from '../i18n';

const WIDTH = 428;
const HEIGHT = 220;
const TOP = 10;
const BOTTOM = HEIGHT - 10;

function valueToY(value: number): number {
  const clamped = Math.max(-1, Math.min(1, value));
  return BOTTOM - ((clamped + 1) / 2) * (BOTTOM - TOP);
}

function formatPeak(value: number | undefined): string {
  if (!value || !Number.isFinite(value) || value <= 0) return '';
  return `${(20 * Math.log10(value)).toFixed(1)} dBFS`;
}

function buildGrid(): HTMLCanvasElement {
  const grid = document.createElement('canvas');
  grid.width = WIDTH;
  grid.height = HEIGHT;
  const context = grid.getContext('2d');
  if (!context) return grid;

  context.lineWidth = 1;
  for (const value of [0.5, 0, -0.5]) {
    const y = valueToY(value);
    context.strokeStyle = value === 0 ? 'rgba(255,255,255,0.16)' : 'rgba(255,255,255,0.07)';
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(WIDTH, y);
    context.stroke();
  }

  context.strokeStyle = 'rgba(255,255,255,0.045)';
  for (let index = 1; index < 8; index++) {
    const x = (WIDTH * index) / 8;
    context.beginPath();
    context.moveTo(x, TOP);
    context.lineTo(x, BOTTOM);
    context.stroke();
  }

  return grid;
}

export default function WaveformView({
  samples,
  peak,
  color,
}: {
  samples?: number[];
  peak?: number;
  color: string;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const visible = useElementVisible(ref);
  const gridRef = useRef<HTMLCanvasElement | null>(null);

  useEffect(() => {
    if (!visible) return;
    const canvas = ref.current;
    const context = canvas?.getContext('2d');
    if (!context) return;

    if (!gridRef.current) {
      gridRef.current = buildGrid();
    }

    context.clearRect(0, 0, WIDTH, HEIGHT);
    if (gridRef.current) {
      context.drawImage(gridRef.current, 0, 0);
    }

    const values = samples ?? [];
    if (!values.length) {
      context.fillStyle = 'rgba(255,255,255,0.22)';
      context.font = '8px system-ui, sans-serif';
      context.fillText(t('Waiting for signal'), 6, BOTTOM - 6);
      return;
    }

    const step = WIDTH / values.length;
    context.beginPath();
    for (let index = 0; index < values.length; index++) {
      const x = index * step + step / 2;
      const y = valueToY(values[index]);
      if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
    }

    context.lineJoin = 'round';
    context.lineCap = 'round';
    context.strokeStyle = color;
    context.globalAlpha = 0.18;
    context.lineWidth = 7;
    context.stroke();
    context.globalAlpha = 1;
    context.lineWidth = 2.2;
    context.stroke();

    context.fillStyle = '#FF6B6B';
    for (let index = 0; index < values.length; index++) {
      if (Math.abs(values[index]) < 0.999) continue;
      const x = index * step + step / 2;
      context.fillRect(x - 1, values[index] > 0 ? TOP : BOTTOM - 2, 2, 2);
    }
  }, [samples, color, t, visible]);

  const milliseconds = ((samples?.length ?? 0) / 48).toFixed(1);

  return (
    <div className="waveform-view">
      <canvas ref={ref} width={WIDTH} height={HEIGHT} aria-label={t('Waveform')} />
      <div className="spectrum-meta">
        <span>{milliseconds} ms</span>
        <strong>{formatPeak(peak)}</strong>
      </div>
    </div>
  );
}
