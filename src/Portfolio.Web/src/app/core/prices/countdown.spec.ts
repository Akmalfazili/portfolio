import { createCountdown } from './countdown';

describe('createCountdown (F4)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('starts at null', () => {
    const countdown = createCountdown();
    expect(countdown.secondsRemaining()).toBeNull();
  });

  it('counts down by one every second and lands on null after the last tick', () => {
    const countdown = createCountdown();
    countdown.start(3);
    expect(countdown.secondsRemaining()).toBe(3);

    vi.advanceTimersByTime(1000);
    expect(countdown.secondsRemaining()).toBe(2);

    vi.advanceTimersByTime(1000);
    expect(countdown.secondsRemaining()).toBe(1);

    vi.advanceTimersByTime(1000);
    expect(countdown.secondsRemaining()).toBeNull();
  });

  it('does not go negative or keep ticking once it reaches null', () => {
    const countdown = createCountdown();
    countdown.start(1);
    vi.advanceTimersByTime(1000);
    expect(countdown.secondsRemaining()).toBeNull();

    vi.advanceTimersByTime(5000);
    expect(countdown.secondsRemaining()).toBeNull();
  });

  it('a second start() call restarts from the new value rather than stacking timers', () => {
    const countdown = createCountdown();
    countdown.start(10);
    vi.advanceTimersByTime(1000);
    expect(countdown.secondsRemaining()).toBe(9);

    countdown.start(3);
    expect(countdown.secondsRemaining()).toBe(3);

    vi.advanceTimersByTime(1000);
    // If the first interval had not been cleared, this would have ticked
    // twice (once per interval) and read 1, not 2.
    expect(countdown.secondsRemaining()).toBe(2);
  });

  it('stop() cancels a running countdown so it never reaches null on its own', () => {
    const countdown = createCountdown();
    countdown.start(5);
    countdown.stop();

    vi.advanceTimersByTime(10_000);
    // Mirrors the original PriceStore.teardown() behaviour: stop() only
    // clears the interval handle, it does not reset the last value.
    expect(countdown.secondsRemaining()).toBe(5);
  });

  it('stop() is safe to call when no countdown is running', () => {
    const countdown = createCountdown();
    expect(() => countdown.stop()).not.toThrow();
    expect(countdown.secondsRemaining()).toBeNull();
  });
});
