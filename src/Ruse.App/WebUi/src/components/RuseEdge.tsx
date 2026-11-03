import { BaseEdge, EdgeLabelRenderer, getBezierPath } from '@xyflow/react';
import type { EdgeProps } from '@xyflow/react';
import { useNodeActive } from '../telemetry';

export default function RuseEdge({
  id, source, sourceX, sourceY, targetX, targetY,
  sourcePosition, targetPosition, style, label, markerEnd, data,
}: EdgeProps) {
  const active = useNodeActive(source);
  const sidechain = (data as { kind?: string } | undefined)?.kind === 'sidechain';
  const [path, labelX, labelY] = getBezierPath({
    sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, curvature: 0.28,
  });
  const color = typeof style?.stroke === 'string' ? style.stroke : '#5B8DEF';
  const baseStyle = active
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
