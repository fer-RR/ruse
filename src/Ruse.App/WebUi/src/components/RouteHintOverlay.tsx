import { useEffect, useMemo, useRef, useState } from 'react';
import { ViewportPortal } from '@xyflow/react';
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

    let next: string | null = null;
    for (const [id, since] of soundStarted.current) {
      if (now - since >= HINT_DELAY_MS) {
        next = id;
        break;
      }
    }

    setHintId(current => current === next ? current : next);
  }, [connected, nodes, telemetry]);

  const node = hintId ? nodes.find(candidate => candidate.id === hintId) : undefined;
  if (!node) return null;

  const width = node.measured?.width ?? 204;
  const height = node.measured?.height ?? 140;
  const placeLeft = node.position.x > 520;
  const bubbleWidth = 224;
  const gap = 26;
  const left = placeLeft
    ? node.position.x - bubbleWidth - gap
    : node.position.x + width + gap;
  const top = node.position.y + Math.max(0, height / 2 - 20);

  return (
    <ViewportPortal>
      <div
        className="route-hint-outline"
        style={{ left: node.position.x - 6, top: node.position.y - 6, width: width + 12, height: height + 12 }}
      />
      <div className={`route-hint ${placeLeft ? 'left' : 'right'}`} style={{ left, top }}>
        <span className="route-hint-dash" />
        <span className="route-hint-bubble">
          {t('Drag from the OUT dot on a node to the IN dot on another node. The cable carries the sound.')}
        </span>
      </div>
    </ViewportPortal>
  );
}
