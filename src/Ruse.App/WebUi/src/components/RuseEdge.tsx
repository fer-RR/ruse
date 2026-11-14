import { useEffect, useRef } from 'react';
import { BaseEdge, EdgeLabelRenderer, getBezierPath } from '@xyflow/react';
import { CableRope } from '../cablePhysics';
import { Lock } from 'lucide-react';
import type { EdgeProps } from '@xyflow/react';
import { useNodeActive } from '../telemetry';

const reducedMotion = typeof window !== 'undefined' &&
  window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;

export default function RuseEdge({
  id, source, sourceX, sourceY, targetX, targetY,
  sourcePosition, targetPosition, style, label, markerEnd, data,
}: EdgeProps) {
  const physics = (data as { physics?: boolean } | undefined)?.physics === true && !reducedMotion;
  const pathRef = useRef<SVGPathElement>(null);
  const interactionRef = useRef<SVGPathElement>(null);
  const flowRef = useRef<SVGPathElement>(null);
  const ropeRef = useRef<CableRope | null>(null);
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


  useEffect(() => {
    if (!physics) return;
    const rope = new CableRope(path => {
      pathRef.current?.setAttribute('d', path);
      interactionRef.current?.setAttribute('d', path);
      flowRef.current?.setAttribute('d', path);
    });
    rope.setAnchors(sourceX, sourceY, targetX, targetY);
    ropeRef.current = rope;
    return () => {
      rope.dispose();
      if (ropeRef.current === rope) ropeRef.current = null;
    };
  }, [physics]);

  useEffect(() => {
    ropeRef.current?.setAnchors(sourceX, sourceY, targetX, targetY);
  }, [physics, sourceX, sourceY, targetX, targetY]);

  return (
    <>
      {physics ? (
        <>
          <path ref={pathRef} d={path} fill="none" className="react-flow__edge-path" style={lineStyle} markerEnd={markerEnd} />
          <path ref={interactionRef} d={path} fill="none" strokeOpacity={0} strokeWidth={22} className="react-flow__edge-interaction" />
          <path ref={flowRef} d={path} className={`ruse-edge-flow ${active ? 'active' : ''}`} style={{ stroke: color, opacity: active ? 0.72 : 0 }} />
        </>
      ) : (
        <>
          <BaseEdge id={id} path={path} style={lineStyle} markerEnd={markerEnd} interactionWidth={22} />
          <path
            d={path}
            className={`ruse-edge-flow ${active ? 'active' : ''}`}
            style={{ stroke: color, opacity: active ? 0.72 : 0 }}
          />
        </>
      )}
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
