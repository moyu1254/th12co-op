"""Read-only hook/caller checks against the two supported retail executables."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

PROFILES = {
    'retail-1.00a': ('e2939ba3f92e427216a619191a682a8197cc59a64eab99302a7b98811d6120a9', 0),
    'retail-1.00b': ('d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628', 32),
}
HOOKS = {
    0x445e41: '8b4d288d4528', 0x445e7b: '8b4d288d4528',
    0x413170: '8b44240456', 0x413820: '558bec83e4f8',
    0x413e60: 'f683b816000010', 0x41319d: '8b3681a6f8260000fffffdff',
    0x43918b: '8bc65e5dc21400', 0x43a3b1: '8b471001442410',
    0x43a1de: 'dd05f83c4a0003d8',
    0x439f50: '8b473085c0', 0x43a1fc: '8b4f20f6c101',
    0x4131c1: '8b74240883c650',
}


def verify(path):
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    profile = next(((name, shift) for name, (expected, shift) in PROFILES.items() if digest == expected), None)
    if profile is None:
        raise ValueError('Use an unmodified supported retail executable; no inferred layout is accepted')
    name, shift = profile
    pe = struct.unpack_from('<I', raw, 0x3c)[0]
    sections = struct.unpack_from('<H', raw, pe + 6)[0]
    optional_size = struct.unpack_from('<H', raw, pe + 20)[0]
    base = struct.unpack_from('<I', raw, pe + 24 + 28)[0]
    table = pe + 24 + optional_size

    def read(address, length):
        rva = address - base
        for n in range(sections):
            size, va, raw_size, offset = struct.unpack_from('<4I', raw, table + n * 40 + 8)
            if va <= rva and rva + length <= va + raw_size:
                return raw[offset + rva - va:offset + rva - va + length]
        raise ValueError('Address is outside backed PE sections')

    for address, expected in HOOKS.items():
        assert read(address + shift, len(bytes.fromhex(expected))) == bytes.fromhex(expected), hex(address)
    for caller in (0x407ff5, 0x408090, 0x408fc8, 0x436edc, 0x43aa8a, 0x469837):
        instruction = read(caller + shift, 5)
        assert instruction[0] == 0xe8
        assert caller + shift + 5 + struct.unpack_from('<i', instruction, 1)[0] == 0x4390d0 + shift
    # AsciiManager precedes the version's 0x20 insertion. The boss registry
    # stores the full Enemy*, as shown by the native ECL write and overlay read.
    assert read(0x4014f0, 6) == bytes.fromhex('8b917c8f0100')
    assert read(0x417b09 + shift, 6) == bytes.fromhex('8b834c170000')
    assert read(0x417b1f + shift, 4) == bytes.fromhex('8944b11c')
    assert read(0x40dc30 + shift, 9) == bytes.fromhex('8b401cd98074100000')
    return dict(profile=name, exeSha256=digest, hooks=len(HOOKS), sourceCallers=6, unchangedAscii=True, fullEnemyBossRegistry=True, passed=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executables', type=Path, nargs='+')
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    result = dict(passed=True, profiles=[verify(path) for path in args.executables])
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2), encoding='utf8')
    print(json.dumps(result))
