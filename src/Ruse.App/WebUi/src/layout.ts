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
