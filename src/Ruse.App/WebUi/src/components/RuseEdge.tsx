import { BaseEdge, EdgeLabelRenderer, getBezierPath } from '@xyflow/react';
import { Lock } from 'lucide-react';
import type { EdgeProps } from '@xyflow/react';
import { useNodeActive } from '../telemetry';

export default function RuseEdge({
  id, source, sourceX, sourceY, targetX, targetY,
  sourcePosition, targetPosition, style, label, markerEnd, data,
}: EdgeProps) {
  const active = useNodeActive(source);
  const sidechain = (data as { kind?: string } | undefined)?.kind === 'sidechain';
  const native = (data as { native?: boolean; nativeRate?: number } | undefined)?.native === true;
  const nativeRate = (data as { nativeRate?: number } | undefined)?.nativeRate;
  const rejected = (data as { rejected?: boolean } | undefined)?.rejected === true;
  const [path, labelX, labelY] = getBezierPath({
    sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, curvature: 0.28,
  });
  const color = typeof style?.stroke === 'string' ? style.stroke : '#5B8DEF';
  const baseStyle = rejected
    ? { ...style, stroke: '#ff5b5b', strokeWidth: 3, filter: 'drop-shadow(0 0 7px #ff5b5b)', opacity: 1 }
    : active
      ? style
      : { ...style, filter: 'saturate(0.18)', opacity: 0.48 };
  const lineStyle = sidechain ? { ...baseStyle, strokeDasharray: '5 4' } : baseStyle;

  return (
    <>
      <BaseEdge id={id} path={path} style={lineStyle} markerEnd={markerEnd} interactionWidth={22} />
      <path
        d={path}
        className={`ruse-edge-flow ${active ? 'active' : ''}`}
        style={{ stroke: color, opacity: active ? 0.72 : 0 }}
      />
      {sidechain && (
        <EdgeLabelRenderer>
          <div className="edge-key-label" style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}>
            KEY
          </div>
        </EdgeLabelRenderer>
      )}
      {native && (
        <EdgeLabelRenderer>
          <div className="edge-gain-label" style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY - 14}px)`, color: 'var(--cyan)', display: 'flex', alignItems: 'center', gap: 3 }}>
            <Lock size={9} /> {nativeRate ? `${(nativeRate / 1000).toFixed(nativeRate % 1000 === 0 ? 0 : 1)} kHz` : 'NATIVE'}
          </div>
        </EdgeLabelRenderer>
      )}
      {label && (
        <EdgeLabelRenderer>
          <div className="edge-gain-label" style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}>
            {label}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}
