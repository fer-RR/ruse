type RopePoint = { x: number; y: number; px: number; py: number };

const SEGMENTS = 8;
const POINT_COUNT = SEGMENTS + 1;
const ITERATIONS = 4;
const GRAVITY = 620;
const DAMPING = 0.92;
const SLEEP_FRAMES = 30;

let frame = 0;
let lastFrame = 0;
const ropes = new Set<CableRope>();

function wake() {
  if (frame !== 0) return;
  lastFrame = 0;
  frame = requestAnimationFrame(tick);
}

function tick(now: number) {
  const dt = lastFrame === 0 ? 1 / 60 : Math.min(1 / 30, (now - lastFrame) / 1000);
  lastFrame = now;
  let active = false;
  for (const rope of ropes) {
    if (rope.step(dt)) active = true;
  }

  if (active) {
    frame = requestAnimationFrame(tick);
  } else {
    frame = 0;
    lastFrame = 0;
  }
}

/** A small, shared-RAF Verlet rope used by the optional cable physics. */
export class CableRope {
  private readonly points: RopePoint[] = Array.from({ length: POINT_COUNT }, () => ({
    x: 0, y: 0, px: 0, py: 0,
  }));
  private readonly draw: (path: string) => void;
  private sourceX = 0;
  private sourceY = 0;
  private targetX = 0;
  private targetY = 0;
  private restLength = 1;
  private frameCount = 0;
  private sleeping = false;
  private initialized = false;
  private lastPath = '';

  constructor(draw: (path: string) => void) {
    this.draw = draw;
    ropes.add(this);
  }

  setAnchors(sourceX: number, sourceY: number, targetX: number, targetY: number) {
    const changed = sourceX !== this.sourceX || sourceY !== this.sourceY ||
      targetX !== this.targetX || targetY !== this.targetY;
    if (!changed && this.initialized) return;

    this.sourceX = sourceX;
    this.sourceY = sourceY;
    this.targetX = targetX;
    this.targetY = targetY;
    this.restLength = Math.max(24, Math.hypot(targetX - sourceX, targetY - sourceY) / SEGMENTS);

    if (!this.initialized) {
      this.seed();
      this.initialized = true;
      this.lastPath = this.path();
      this.draw(this.lastPath);
      this.sleeping = true;
      this.frameCount = 0;
      return;
    }

    this.sleeping = false;
    this.frameCount = 0;
    wake();
  }

  labelPoint(): { x: number; y: number } {
    const point = this.points[Math.floor(this.points.length / 2)];
    return { x: point.x, y: point.y };
  }

  step(dt: number): boolean {
    if (this.sleeping) return false;

    const gravity = GRAVITY * dt * dt;
    for (let i = 1; i < this.points.length - 1; i++) {
      const point = this.points[i];
      const vx = (point.x - point.px) * DAMPING;
      const vy = (point.y - point.py) * DAMPING;
      point.px = point.x;
      point.py = point.y;
      point.x += vx;
      point.y += vy + gravity;
    }

    this.points[0].x = this.points[0].px = this.sourceX;
    this.points[0].y = this.points[0].py = this.sourceY;
    const last = this.points.length - 1;
    this.points[last].x = this.points[last].px = this.targetX;
    this.points[last].y = this.points[last].py = this.targetY;

    for (let iteration = 0; iteration < ITERATIONS; iteration++) {
      for (let i = 0; i < this.points.length - 1; i++) {
        const a = this.points[i];
        const b = this.points[i + 1];
        let dx = b.x - a.x;
        let dy = b.y - a.y;
        const distance = Math.hypot(dx, dy) || 1;
        const correction = (distance - this.restLength) / distance;
        const aWeight = i === 0 ? 0 : 0.5;
        const bWeight = i + 1 === last ? 0 : 0.5;
        if (aWeight > 0) {
          a.x += dx * correction * aWeight;
          a.y += dy * correction * aWeight;
        }
        if (bWeight > 0) {
          b.x -= dx * correction * bWeight;
          b.y -= dy * correction * bWeight;
        }
      }
    }

    const path = this.path();
    const changed = path !== this.lastPath;
    if (changed) {
      this.lastPath = path;
      this.draw(path);
      this.frameCount = 0;
    } else {
      this.frameCount++;
      if (this.frameCount >= SLEEP_FRAMES) {
        this.sleeping = true;
        return false;
      }
    }

    return true;
  }

  dispose() {
    ropes.delete(this);
  }

  private seed() {
    const dx = this.targetX - this.sourceX;
    const dy = this.targetY - this.sourceY;
    const distance = Math.hypot(dx, dy);
    const sag = Math.min(42, distance * 0.08);
    for (let i = 0; i < this.points.length; i++) {
      const t = i / (this.points.length - 1);
      const point = this.points[i];
      point.x = point.px = this.sourceX + dx * t;
      point.y = point.py = this.sourceY + dy * t + Math.sin(Math.PI * t) * sag;
    }
  }

  private path(): string {
    const points = this.points;
    let result = `M ${points[0].x.toFixed(1)} ${points[0].y.toFixed(1)}`;
    for (let i = 1; i < points.length - 1; i++) {
      const x = (points[i].x + points[i + 1].x) * 0.5;
      const y = (points[i].y + points[i + 1].y) * 0.5;
      result += ` Q ${points[i].x.toFixed(1)} ${points[i].y.toFixed(1)} ${x.toFixed(1)} ${y.toFixed(1)}`;
    }

    const last = points[points.length - 1];
    return result + ` L ${last.x.toFixed(1)} ${last.y.toFixed(1)}`;
  }
}
