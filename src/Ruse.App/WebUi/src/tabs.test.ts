import { describe, expect, it } from 'vitest';
import { nextTabId } from './tabs';

const ids = ['one', 'two', 'three'] as const;

describe('nextTabId', () => {
  it('moves forward and wraps', () => {
    expect(nextTabId(ids, 'one', 'ArrowRight')).toBe('two');
    expect(nextTabId(ids, 'two', 'ArrowDown')).toBe('three');
    expect(nextTabId(ids, 'three', 'ArrowRight')).toBe('one');
  });

  it('moves backward and wraps', () => {
    expect(nextTabId(ids, 'one', 'ArrowLeft')).toBe('three');
    expect(nextTabId(ids, 'three', 'ArrowUp')).toBe('two');
  });

  it('supports Home and End', () => {
    expect(nextTabId(ids, 'two', 'Home')).toBe('one');
    expect(nextTabId(ids, 'two', 'End')).toBe('three');
  });

  it('ignores unrelated keys', () => {
    expect(nextTabId(ids, 'two', 'Enter')).toBeNull();
    expect(nextTabId([], 'one' as string, 'ArrowRight')).toBeNull();
  });
});
