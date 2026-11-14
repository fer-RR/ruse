import { useEffect, useMemo, useRef, useState } from 'react';
import { useStore, ViewportPortal } from '@xyflow/react';
import { useI18n } from '../i18n';
import { useGlobalTelemetry } from '../telemetry';
import type { LinkModel } from '../types';
import type { RuseFlowNode } from '../flow';

const SOUND_THRESHOLD = 0.012;
const HINT_DELAY_MS = 4000;

export default function RouteHintOverlay({ nodes, links }: { nodes: RuseFlowNode[]; links: LinkModel[] }) {
  const { t } = useI18n();
  const telemetry = useGlobalTelemetry();
  const [hintId, setHintId] = useState<string | null>(null);
  const soundStarted = useRef(new Map<string, number>());
  const dismissed = useRef(new Set<string>());
  const width = useStore(state => state.width);
  const height = useStore(state => state.height);
  const transform = useStore(state => state.transform);

  const connected = useMemo(() => {
    const ids = new Set<string>();
    for (const link of links) {
      if (link.kind === 'sidechain' || link.enabled === false) continue;
      ids.add(link.from);
    }
    return ids;
  }, [links]);

  useEffect(() => {
    const now = performance.now();
    const sounding = new Set<string>();
    for (const node of nodes) {
      if (node.data.kind !== 'source' || node.data.muted || connected.has(node.id)) continue;
      const level = telemetry.meters[node.id]?.level ?? 0;
      if (level < SOUND_THRESHOLD) continue;
      sounding.add(node.id);
      if (!soundStarted.current.has(node.id)) soundStarted.current.set(node.id, now);
    }

    for (const id of soundStarted.current.keys()) {
      if (!sounding.has(id)) soundStarted.current.delete(id);
    }

    for (const id of dismissed.current) {
      if (!sounding.has(id)) dismissed.current.delete(id);
    }
    let next: string | null = null;
    for (const [id, since] of soundStarted.current) {
      if (now - since >= HINT_DELAY_MS && !dismissed.current.has(id)) {
        next = id;
        break;
      }
    }

    setHintId(current => current === next ? current : next);
  }, [connected, nodes, telemetry]);

  useEffect(() => {
    if (!hintId) return;
    const timer = window.setTimeout(() => {
      dismissed.current.add(hintId);
      setHintId(null);
    }, 8000);
    return () => window.clearTimeout(timer);
  }, [hintId]);

  const node = hintId ? nodes.find(candidate => candidate.id === hintId) : undefined;
  if (!node) return null;

  const nodeWidth = node.measured?.width ?? 204;
  const nodeHeight = node.measured?.height ?? 140;
  const [translateX, translateY, zoom] = transform;
  const bubbleWidth = 224;
  const gap = 26;
  const screenRight = (node.position.x + nodeWidth) * zoom + translateX;
  const placeLeft = width - screenRight < (bubbleWidth + gap) * zoom;
  const left = placeLeft
    ? node.position.x - bubbleWidth - gap
    : node.position.x + nodeWidth + gap;
  const viewportTop = -translateY / zoom;
  const viewportBottom = (height - translateY) / zoom;
  const top = Math.min(Math.max(node.position.y + Math.max(0, nodeHeight / 2 - 20), viewportTop + 12), viewportBottom - 72);

  return (
    <ViewportPortal>
      <div
        className="route-hint-outline"
        style={{ left: node.position.x - 6, top: node.position.y - 6, width: nodeWidth + 12, height: nodeHeight + 12 }}
      />
      <div className={`route-hint ${placeLeft ? 'left' : 'right'}`} style={{ left, top }}>
        <span className="route-hint-dash" />
        <span className="route-hint-bubble" role="status">
          {t('Drag from the OUT dot on a node to the IN dot on another node. The cable carries the sound.')}
        </span>
      </div>
    </ViewportPortal>
  );
}
