export interface NodeRect {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

const GRID = 20;
const GAP = 12;

function intersects(a: NodeRect, b: NodeRect): boolean {
  return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;
}

function snap(value: number): number {
  return Math.round(value / GRID) * GRID;
}

/** Finds the closest grid slot that does not cover another node. */
export function nearestFreePosition(moving: NodeRect, others: NodeRect[]): { x: number; y: number } {
  const obstacles = others.filter(node => node.id !== moving.id);
  if (!obstacles.some(node => intersects(moving, node))) {
    return { x: moving.x, y: moving.y };
  }

  const xs = new Set<number>([snap(moving.x)]);
  const ys = new Set<number>([snap(moving.y)]);
  for (const obstacle of obstacles) {
    xs.add(snap(obstacle.x - moving.width - GAP));
    xs.add(snap(obstacle.x + obstacle.width + GAP));
    ys.add(snap(obstacle.y - moving.height - GAP));
    ys.add(snap(obstacle.y + obstacle.height + GAP));
  }

  const candidates = [...xs].flatMap(x => [...ys].map(y => ({
    x,
    y,
    distance: (x - moving.x) ** 2 + (y - moving.y) ** 2,
  }))).sort((a, b) => a.distance - b.distance);

  for (const candidate of candidates) {
    const rect = { ...moving, x: candidate.x, y: candidate.y };
    if (!obstacles.some(node => intersects(rect, node))) {
      return { x: candidate.x, y: candidate.y };
    }
  }

  const farRight = Math.max(...obstacles.map(node => node.x + node.width + GAP), moving.x);
  return { x: snap(farRight), y: snap(moving.y) };
}


export type ChainLayoutKind = 'source' | 'modifier' | 'output' | 'network' | 'text';

export interface ChainLayoutNode {
  id: string;
  kind: ChainLayoutKind;
  width?: number;
  height?: number;
}

export interface ChainLayoutLink {
  from: string;
  to: string;
  kind?: string;
}

export interface ChainLayoutPosition {
  id: string;
  x: number;
  y: number;
}

const LAYOUT_ORIGIN = 80;
const LAYOUT_COLUMN_MIN = 204;
const LAYOUT_COLUMN_GAP = 100;
const LAYOUT_ROW_GAP = 44;
const MAX_LAYOUT_PATHS = 256;

/**
 * Arranges a routing graph as horizontal chains. Parallel branches get their
 * own rows, while a shared source or effect keeps a single position.
 */
export function chainLayout(nodes: ChainLayoutNode[], links: ChainLayoutLink[]): ChainLayoutPosition[] {
  const byId = new Map(nodes.map(node => [node.id, node]));
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();

  const addEdge = (map: Map<string, string[]>, from: string, to: string) => {
    const values = map.get(from);
    if (values) {
      if (!values.includes(to)) values.push(to);
    } else {
      map.set(from, [to]);
    }
  };

  for (const link of links) {
    if (link.kind === 'sidechain') continue;
    const source = byId.get(link.from);
    const target = byId.get(link.to);
    if (!source || !target || source.id === target.id) continue;
    addEdge(outgoing, source.id, target.id);
    addEdge(incoming, target.id, source.id);
  }

  const rank = new Map<string, number>();
  const visiting = new Set<string>();
  const rankOf = (id: string): number => {
    const cached = rank.get(id);
    if (cached !== undefined) return cached;
    const node = byId.get(id);
    if (!node) return 0;
    if (node.kind === 'source' || node.kind === 'network') {
      rank.set(id, 0);
      return 0;
    }

    if (visiting.has(id)) return 0;
    visiting.add(id);
    let best = 0;
    for (const parent of incoming.get(id) ?? []) {
      best = Math.max(best, rankOf(parent) + 1);
    }
    visiting.delete(id);
    const value = Math.max(1, best);
    rank.set(id, value);
    return value;
  };

  for (const node of nodes) rankOf(node.id);

  let maxModifierRank = 0;
  for (const node of nodes) {
    if (node.kind === 'modifier') {
      maxModifierRank = Math.max(maxModifierRank, rank.get(node.id) ?? 1);
    }
  }
  const outputColumn = maxModifierRank + 1;
  const columnOf = (node: ChainLayoutNode): number =>
    node.kind === 'source' || node.kind === 'network' ? 0 : node.kind === 'output' ? outputColumn : Math.max(1, rank.get(node.id) ?? 1);

  const paths: string[][] = [];
  const pathKeys = new Set<string>();
  const addPath = (path: string[]) => {
    const key = path.join('>');
    if (pathKeys.has(key)) return;
    pathKeys.add(key);
    paths.push(path);
  };

  const visit = (id: string, path: string[], seen: Set<string>) => {
    if (paths.length >= MAX_LAYOUT_PATHS) return;
    let advanced = false;
    for (const next of outgoing.get(id) ?? []) {
      if (seen.has(next)) continue;
      const target = byId.get(next);
      if (!target || target.kind === 'source') continue;
      advanced = true;
      const nextPath = [...path, next];
      if (target.kind === 'output') {
        addPath(nextPath);
      } else {
        const nextSeen = new Set(seen);
        nextSeen.add(next);
        visit(next, nextPath, nextSeen);
      }
    }

    if (!advanced && path.length > 1) addPath(path);
  };

  for (const source of nodes) {
    if (source.kind !== 'source' && source.kind !== 'network') continue;
    visit(source.id, [source.id], new Set([source.id]));
  }

  const rowForNode = new Map<string, number>();
  const rows: ChainLayoutNode[][] = [];
  for (const path of paths) {
    let row = -1;
    for (const id of path) {
      if (rowForNode.has(id)) continue;
      const node = byId.get(id);
      if (!node) continue;
      if (row < 0) {
        row = rows.length;
        rows.push([]);
      }

      rowForNode.set(id, row);
      rows[row].push(node);
    }
  }

  for (const node of nodes) {
    if (rowForNode.has(node.id)) continue;
    const row = rows.length;
    rows.push([node]);
    rowForNode.set(node.id, row);
  }

  const columnWidths = new Map<number, number>();
  for (const node of nodes) {
    const column = columnOf(node);
    columnWidths.set(column, Math.max(columnWidths.get(column) ?? 0, node.width ?? LAYOUT_COLUMN_MIN));
  }

  const columnX = new Map<number, number>();
  let nextX = LAYOUT_ORIGIN;
  const maxColumn = Math.max(0, ...nodes.map(columnOf));
  for (let column = 0; column <= maxColumn; column++) {
    columnX.set(column, nextX);
    nextX += Math.max(LAYOUT_COLUMN_MIN, columnWidths.get(column) ?? 0) + LAYOUT_COLUMN_GAP;
  }

  const rowY = new Map<number, number>();
  let nextY = LAYOUT_ORIGIN;
  for (let row = 0; row < rows.length; row++) {
    rowY.set(row, nextY);
    const height = rows[row].reduce((max, node) => Math.max(max, node.height ?? 140), 120);
    nextY += height + LAYOUT_ROW_GAP;
  }

  return nodes.map(node => ({
    id: node.id,
    x: columnX.get(columnOf(node)) ?? LAYOUT_ORIGIN,
    y: rowY.get(rowForNode.get(node.id) ?? 0) ?? LAYOUT_ORIGIN,
  }));
}
