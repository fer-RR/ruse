import { BaseEdge, EdgeLabelRenderer, getBezierPath } from '@xyflow/react';
import type { EdgeProps } from '@xyflow/react';
import { useNodeTelemetry } from '../telemetry';

export default function RuseEdge({
  id, source, sourceX, sourceY, targetX, targetY,
  sourcePosition, targetPosition, style, label, markerEnd,
}: EdgeProps) {
  const telemetry = useNodeTelemetry(source);
  const level = Math.min(1, Math.max(0, telemetry?.level ?? 0));
  const active = level > 0.01;
  const [path, labelX, labelY] = getBezierPath({
    sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, curvature: 0.28,
  });
  const color = typeof style?.stroke === 'string' ? style.stroke : '#5B8DEF';

  return (
    <>
      <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} interactionWidth={22} />
      <path
        d={path}
        className={`ruse-edge-flow ${active ? 'active' : ''}`}
        style={{ stroke: color, opacity: active ? 0.35 + level * 0.5 : 0 }}
      />
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
