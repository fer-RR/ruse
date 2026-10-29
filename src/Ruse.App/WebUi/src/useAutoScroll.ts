import { useLayoutEffect, useRef } from 'react';

/**
 * Keeps a scrolling transcript pinned to the newest text until the user scrolls
 * away. Scrolling back to the bottom re-arms it.
 */
export function useAutoScroll(content: string, threshold = 24) {
  const ref = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element || !pinned.current) return;

    element.scrollTop = element.scrollHeight;
  }, [content]);

  const onScroll = () => {
    const element = ref.current;
    if (!element) return;

    pinned.current = element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
  };

  return { ref, onScroll };
}
