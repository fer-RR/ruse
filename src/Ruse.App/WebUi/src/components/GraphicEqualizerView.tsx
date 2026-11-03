import { useEffect, useMemo, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent } from 'react';
import { send } from '../bridge';
import type { ParameterModel } from '../types';

const MIN_DB = -12;
const MAX_DB = 12;
const MIN_HZ = 20;
const MAX_HZ = 20000;

function parseFrequency(name: string): number {
  const match = name.match(/([\d.]+)\s*(k?hz)/i);
  if (!match) return 1000;
  const value = Number(match[1]);
  return match[2].toLowerCase().startsWith('k') ? value * 1000 : value;
}

function shortLabel(frequency: number): string {
  return frequency >= 1000 ? `${Number((frequency / 1000).toFixed(1))}k` : `${Math.round(frequency)}`;
}

function xFor(frequency: number, width: number): number {
  return (Math.log(frequency / MIN_HZ) / Math.log(MAX_HZ / MIN_HZ)) * width;
}

function yFor(db: number, top: number, bottom: number): number {
  return bottom - ((db - MIN_DB) / (MAX_DB - MIN_DB)) * (bottom - top);
}

function dbFor(y: number, top: number, bottom: number): number {
  const ratio = 1 - (y - top) / Math.max(1, bottom - top);
  return Math.round(Math.max(MIN_DB, Math.min(MAX_DB, MIN_DB + ratio * (MAX_DB - MIN_DB))) * 2) / 2;
}

export default function GraphicEqualizerView({
  nodeId,
  parameters,
  accent,
}: {
  nodeId: string;
  parameters: ParameterModel[];
  accent: string;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [values, setValues] = useState<Record<number, number>>({});
  const dragging = useRef(false);
  const frequencies = useMemo(() => parameters.map(parameter => parseFrequency(parameter.name)), [parameters]);

  useEffect(() => {
    if (dragging.current) return;
    const next: Record<number, number> = {};
    parameters.forEach(parameter => { next[parameter.parameterIndex] = parameter.value; });
    setValues(next);
  }, [parameters]);

  useEffect(() => {
    const element = canvas.current;
    const context = element?.getContext('2d');
    if (!element || !context) return;
    const width = element.width;
    const height = element.height;
    const top = 8;
    const bottom = height - 18;

    context.clearRect(0, 0, width, height);
    context.font = '8px Consolas, monospace';
    context.textBaseline = 'top';

    for (const db of [MAX_DB, 6, 0, -6, MIN_DB]) {
      const y = yFor(db, top, bottom);
      context.strokeStyle = db === 0 ? 'rgba(255,255,255,0.2)' : 'rgba(255,255,255,0.07)';
      context.lineWidth = 1;
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(width, y);
      context.stroke();
      context.fillStyle = 'rgba(255,255,255,0.28)';
      context.fillText(`${db > 0 ? '+' : ''}${db}`, 2, y - 9);
    }

    frequencies.forEach(frequency => {
      const x = xFor(frequency, width);
      context.strokeStyle = 'rgba(255,255,255,0.045)';
      context.beginPath();
      context.moveTo(x, top);
      context.lineTo(x, bottom);
      context.stroke();
      context.fillStyle = 'rgba(255,255,255,0.3)';
      const label = shortLabel(frequency);
      context.fillText(label, x - context.measureText(label).width / 2, bottom + 4);
    });

    const points = parameters.map((parameter, index) => ({
      x: xFor(frequencies[index], width),
      y: yFor(values[parameter.parameterIndex] ?? 0, top, bottom),
    }));
    if (points.length === 0) return;

    context.beginPath();
    context.moveTo(0, yFor(0, top, bottom));
    context.lineTo(points[0].x, points[0].y);
    for (let i = 0; i < points.length - 1; i++) {
      const current = points[i];
      const next = points[i + 1];
      const midX = (current.x + next.x) / 2;
      const midY = (current.y + next.y) / 2;
      context.quadraticCurveTo(current.x, current.y, midX, midY);
    }
    context.lineTo(points[points.length - 1].x, points[points.length - 1].y);
    context.lineTo(width, yFor(0, top, bottom));
    context.closePath();
    context.fillStyle = `${accent}22`;
    context.fill();

    context.beginPath();
    context.moveTo(points[0].x, points[0].y);
    for (let i = 0; i < points.length - 1; i++) {
      const current = points[i];
      const next = points[i + 1];
      const midX = (current.x + next.x) / 2;
      const midY = (current.y + next.y) / 2;
      context.quadraticCurveTo(current.x, current.y, midX, midY);
    }
    context.lineTo(points[points.length - 1].x, points[points.length - 1].y);
    context.strokeStyle = accent;
    context.lineWidth = 1.7;
    context.stroke();

    for (const point of points) {
      context.beginPath();
      context.arc(point.x, point.y, 3.2, 0, Math.PI * 2);
      context.fillStyle = '#11161e';
      context.fill();
      context.strokeStyle = accent;
      context.lineWidth = 1.4;
      context.stroke();
    }
  }, [values, parameters, frequencies, accent]);

  const updateFromPointer = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const element = canvas.current;
    if (!element) return;
    const rect = element.getBoundingClientRect();
    const width = element.width;
    const height = element.height;
    const x = ((event.clientX - rect.left) / Math.max(1, rect.width)) * width;
    const y = ((event.clientY - rect.top) / Math.max(1, rect.height)) * height;
    const top = 8;
    const bottom = height - 18;

    let nearest = 0;
    let distance = Infinity;
    frequencies.forEach((frequency, index) => {
      const pointX = xFor(frequency, width);
      const delta = Math.abs(pointX - x);
      if (delta < distance) {
        distance = delta;
        nearest = index;
      }
    });

    const parameter = parameters[nearest];
    if (!parameter) return;
    const value = dbFor(y, top, bottom);
    setValues(current => ({ ...current, [parameter.parameterIndex]: value }));
    send('param', {
      id: nodeId,
      processor: parameter.processorIndex,
      parameter: parameter.parameterIndex,
      value,
    });
  };

  return (
    <div className="graphic-eq-view nodrag">
      <canvas
        ref={canvas}
        width={356}
        height={176}
        aria-label="Graphic equalizer"
        onPointerDown={event => {
          dragging.current = true;
          event.currentTarget.setPointerCapture(event.pointerId);
          updateFromPointer(event);
        }}
        onPointerMove={event => {
          if (dragging.current) updateFromPointer(event);
        }}
        onPointerUp={event => {
          dragging.current = false;
          event.currentTarget.releasePointerCapture(event.pointerId);
        }}
        onPointerCancel={() => { dragging.current = false; }}
      />
      <div className="graphic-eq-meta">
        <span>±12 dB</span>
        <span>31 Hz – 16 kHz</span>
      </div>
    </div>
  );
}
