import { useEffect, useMemo, useRef } from 'react';
import { Position, getBezierPath, useReactFlow } from '@xyflow/react';
import type { ConnectionLineComponentProps } from '@xyflow/react';

/**
 * Drives the cable preview from pointer events directly. React Flow still owns
 * connection detection, but its store-driven line can arrive in coarse steps
 * while the canvas is busy; this keeps the line attached to the pointer at the
 * display refresh rate.
 */
export default function RuseConnectionLine({
  fromX, fromY, fromPosition, toX, toY, toPosition,
  connectionLineStyle, connectionStatus,
}: ConnectionLineComponentProps) {
  const flow = useReactFlow();
  const pathRef = useRef<SVGPathElement>(null);
  const fromRef = useRef({ x: fromX, y: fromY, position: fromPosition });
  const toRef = useRef({ x: toX, y: toY, position: toPosition });
  const snappedRef = useRef(false);

  fromRef.current = { x: fromX, y: fromY, position: fromPosition };
  toRef.current = { x: toX, y: toY, position: toPosition };
  snappedRef.current = connectionStatus !== null;

  const initialPath = useMemo(() => getBezierPath({
    sourceX: fromX,
    sourceY: fromY,
    sourcePosition: fromPosition,
    targetX: toX,
    targetY: toY,
    targetPosition: toPosition,
    curvature: 0.28,
  })[0], [fromX, fromY, fromPosition, toX, toY, toPosition]);

  useEffect(() => {
    let frame = 0;
    let pending: { x: number; y: number } | null = null;

    const draw = (x: number, y: number, position: Position) => {
      const [path] = getBezierPath({
        sourceX: fromRef.current.x,
        sourceY: fromRef.current.y,
        sourcePosition: fromRef.current.position,
        targetX: x,
        targetY: y,
        targetPosition: position,
        curvature: 0.28,
      });
      pathRef.current?.setAttribute('d', path);
    };

    const move = (event: PointerEvent) => {
      pending = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        if (!pending) return;

        const target = toRef.current;
        const distance = Math.hypot(pending.x - target.x, pending.y - target.y) * flow.getZoom();
        if (snappedRef.current && distance < 54) {
          draw(target.x, target.y, target.position);
          return;
        }

        const targetPosition = fromRef.current.position === Position.Right
          ? Position.Left
          : Position.Right;
        draw(pending.x, pending.y, targetPosition);
      });
    };

    window.addEventListener('pointermove', move, { passive: true });
    return () => {
      window.removeEventListener('pointermove', move);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [flow]);

  return <path ref={pathRef} d={initialPath} fill="none" className="react-flow__connection-path" style={connectionLineStyle} />;
}
