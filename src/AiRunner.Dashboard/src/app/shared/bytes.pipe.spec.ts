import { BytesPipe, formatBytes } from './bytes.pipe';

describe('formatBytes', () => {
  it('shows bytes below 1 KB as they are', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(512)).toBe('512 B');
  });

  it('picks the largest unit below 1024 with one decimal', () => {
    expect(formatBytes(2048)).toBe('2.0 KB');
    expect(formatBytes(1.5 * 1024 * 1024)).toBe('1.5 MB');
    expect(formatBytes(3 * 1024 ** 3)).toBe('3.0 GB');
    expect(formatBytes(2 * 1024 ** 5)).toBe('2048.0 TB');
  });
});

describe('BytesPipe', () => {
  it('shows "-" for a size that is not known', () => {
    const pipe = new BytesPipe();

    expect(pipe.transform(null)).toBe('-');
    expect(pipe.transform(undefined)).toBe('-');
    expect(pipe.transform(10)).toBe('10 B');
  });
});
