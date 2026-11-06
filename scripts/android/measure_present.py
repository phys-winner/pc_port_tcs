"""Measure unique SurfaceFlinger actual-present timestamps for this game's current SurfaceView."""
import argparse
import json
import math
from pathlib import Path
import statistics
import subprocess
import time


def presents(text):
    result = set()
    for line in text.splitlines()[1:]:
        words = line.split()
        if len(words) == 3 and all(word.isdecimal() for word in words):
            value = int(words[1])
            if 0 < value < (1 << 63) - 1:
                result.add(value)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--serial')
    parser.add_argument('--seconds', type=float, default=6)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        assert presents('8333333\n1 20 2\n2 20 3\n3 0 4\n4 9223372036854775807 5\n5 30 6\n') == {20, 30}
        assert len(presents('1\n1 20 2\n') | presents('1\n1 20 2\n2 30 3\n')) == 2
        print('PRESENT_SAMPLER_PASS cases=2')
        return
    if not args.serial or not args.output or not 2 <= args.seconds <= 30:
        parser.error('--serial, --output and --seconds 2..30 are required')
    if args.output.exists() or args.output.with_suffix('.samples.txt').exists():
        raise ValueError('Capture outputs already exist')

    def shell(command):
        result = subprocess.run(['adb', '-s', args.serial, 'shell', command], capture_output=True, text=True, timeout=15, check=True)
        return result.stdout

    package = 'com.chillasart.theclosingshift.port'
    pid = shell('pidof ' + package).strip()
    assert pid.isdecimal(), 'Expected exactly one game process'
    layers = [line for line in shell('dumpsys SurfaceFlinger --list').splitlines() if package in line and 'SurfaceView[' in line and '(BLAST)' in line]
    assert len(layers) == 1, 'Expected one current game SurfaceView'
    layer = layers[0].split('{', 1)[-1].split(' parentId=', 1)[0]
    previous = presents(shell('dumpsys SurfaceFlinger --latency "' + layer + '"'))
    assert previous, 'Surface has no actual presents'
    cutoff = max(previous)
    timestamps = set()
    raw = []
    start = time.monotonic()
    while time.monotonic() - start < args.seconds:
        time.sleep(.25)
        text = shell('dumpsys SurfaceFlinger --latency "' + layer + '"')
        current = presents(text)
        assert previous & current, 'Ring snapshots stopped overlapping; reject incomplete capture'
        timestamps.update(value for value in current if value > cutoff)
        previous = current
        raw.append(text)
    assert shell('pidof ' + package).strip() == pid, 'Game process changed during capture'
    ordered = sorted(timestamps)
    assert len(ordered) >= 3, 'Too few presented frames'
    intervals = [(b - a) / 1e6 for a, b in zip(ordered, ordered[1:])]
    sorted_intervals = sorted(intervals)
    result = dict(layer=layer, captureSeconds=time.monotonic()-start, frames=len(ordered),
                  fps=(len(ordered)-1)*1e9/(ordered[-1]-ordered[0]), medianMs=statistics.median(intervals),
                  p95Ms=sorted_intervals[math.ceil(.95*len(sorted_intervals))-1], maxMs=max(intervals),
                  timestampsNs=ordered, method='SurfaceFlinger --latency second column; unique actual presents; overlapping ring snapshots')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    args.output.with_suffix('.samples.txt').write_text('\n\n'.join(raw))
    print(json.dumps({k: v for k, v in result.items() if k not in ('layer', 'timestampsNs')}))


if __name__ == '__main__':
    main()
