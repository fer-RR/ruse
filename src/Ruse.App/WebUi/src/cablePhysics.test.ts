import { describe, expect, it } from 'vitest';
import { CableRope } from './cablePhysics';

describe('CableRope', () => {
  it('starts static, wakes only its own rope, and settles', () => {
    const original = globalThis.requestAnimationFrame;
    globalThis.requestAnimationFrame = (() => 0) as typeof requestAnimationFrame;
    let pathA = '';
    let pathB = '';
    const ropeA = new CableRope(next => { pathA = next; });
    const ropeB = new CableRope(next => { pathB = next; });
    try {
      ropeA.setAnchors(0, 0, 300, 0);
      ropeB.setAnchors(0, 100, 300, 100);
      const settledB = pathB;
      expect(pathA).not.toBe('');
      expect(ropeA.step(1 / 60)).toBe(false);
      expect(ropeB.step(1 / 60)).toBe(false);

      ropeA.setAnchors(0, 0, 300, 120);
      expect(ropeA.step(1 / 60)).toBe(true);
      expect(pathA).not.toBe(settledB);
      expect(pathB).toBe(settledB);
      expect(ropeB.step(1 / 60)).toBe(false);

      let awake = true;
      for (let i = 0; i < 600 && awake; i++) {
        awake = ropeA.step(1 / 60);
      }
      expect(awake).toBe(false);
    } finally {
      ropeA.dispose();
      ropeB.dispose();
      globalThis.requestAnimationFrame = original;
    }
  });
});
