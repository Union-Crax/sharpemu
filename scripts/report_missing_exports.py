# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

#!/usr/bin/env python3
"""Reports PS5 symbol-catalog names that have no SysAbiExport handler yet.

Usage (from the repository root):

    python3 scripts/report_missing_exports.py > docs/hle-export-coverage.md

The report has two parts:

1. A hand-curated "high priority" tier: symbols games and their middleware
   (Unity/mono, Unreal, Boehm GC, libc runtimes) are known to import on the
   hot path. Each entry is printed with its NID so it can be implemented
   directly.
2. Per-subsystem counts plus the full missing lists for the smaller,
   compatibility-critical buckets.

The name catalog contains every symbol ever observed (including internal
ones games never import), so bucket totals overstate the real work; the
curated tier is the actionable part.
"""

import hashlib
import re
import struct
from base64 import b64encode as base64enc
from binascii import unhexlify as uhx
from pathlib import Path

NAMES = Path('scripts/ps5_names.txt')
SOURCE_ROOT = Path('src')

EXPORT_NAME_PATTERN = re.compile(r'ExportName\s*=\s*"([^"]+)"')


def name2nid(name: str) -> str:
    symbol = hashlib.sha1(name.encode() + uhx('518D64A635DED8C1E6B039B1C3E55230')).digest()
    id_val = struct.unpack('<Q', symbol[:8])[0]
    return base64enc(uhx('%016x' % id_val), b'+-').rstrip(b'=').decode('utf-8')


def load_catalog() -> set[str]:
    return {line.strip() for line in NAMES.read_text(encoding='utf-8').splitlines() if line.strip()}


def load_registered() -> set[str]:
    registered = set()
    for source in SOURCE_ROOT.rglob('*.cs'):
        registered.update(EXPORT_NAME_PATTERN.findall(source.read_text(encoding='utf-8')))
    return registered


# Symbols commonly imported by games/middleware, grouped by theme. Only names
# present in the catalog and still unregistered are printed.
HIGH_PRIORITY = {
    'Kernel logging / diagnostics': [
        'sceKernelDebugOutText',
        'sceKernelBacktraceSelf',
        'sceKernelError',
    ],
    'Kernel AIO (pak/asset streaming)': [
        'sceKernelAioInitialize',
        'sceKernelAioSetParam',
        'sceKernelAioSubmitReadCommands',
        'sceKernelAioSubmitReadCommandsMultiple',
        'sceKernelAioSubmitWriteCommands',
        'sceKernelAioWaitRequest',
        'sceKernelAioWaitRequests',
        'sceKernelAioPollRequest',
        'sceKernelAioPollRequests',
        'sceKernelAioDeleteRequest',
        'sceKernelAioDeleteRequests',
        'sceKernelAioCancelRequest',
        'sceKernelAioCancelRequests',
    ],
    'Kernel equeue timer events': [
        'sceKernelAddTimerEvent',
        'sceKernelDeleteTimerEvent',
        'sceKernelAddHRTimerEvent',
        'sceKernelDeleteHRTimerEvent',
        'sceKernelAddReadEvent',
        'sceKernelDeleteReadEvent',
        'sceKernelAddFileEvent',
        'sceKernelDeleteFileEvent',
    ],
    'Kernel modules / misc': [
        'sceKernelDlsym',
        'sceKernelGetModuleList',
        'sceKernelStopUnloadModule',
        'sceKernelConfiguredFlexibleMemorySize',
        'sceKernelClearVirtualRangeName',
        'sceKernelOpenEventFlag',
        'sceKernelCloseEventFlag',
        'sceKernelOpenSema',
        'sceKernelCloseSema',
        'sceKernelGetAppInfo',
    ],
    'POSIX pthread flavor (mono/Unity use these heavily)': [
        'pthread_once',
        'pthread_cond_destroy',
        'pthread_condattr_init',
        'pthread_condattr_destroy',
        'pthread_condattr_setclock',
        'pthread_condattr_getclock',
        'pthread_mutex_timedlock',
        'pthread_mutex_init_for_mono',
        'pthread_mutexattr_gettype',
        'pthread_attr_getdetachstate',
        'pthread_attr_setdetachstate',
        'pthread_attr_getstacksize',
        'pthread_attr_getstack',
        'pthread_attr_setstack',
        'pthread_attr_getguardsize',
        'pthread_attr_setguardsize',
        'pthread_getschedparam',
        'pthread_setschedparam',
        'pthread_getaffinity_np',
        'pthread_setaffinity_np',
        'pthread_getname_np',
        'pthread_set_name_np',
        'pthread_main_np',
        'pthread_kill',
        'pthread_cancel',
        'pthread_rwlock_tryrdlock',
        'pthread_rwlock_trywrlock',
        'pthread_rwlock_timedrdlock',
        'pthread_rwlock_timedwrlock',
        'pthread_rwlockattr_init',
        'pthread_rwlockattr_destroy',
        'pthread_barrier_init',
        'pthread_barrier_destroy',
        'pthread_barrier_wait',
    ],
    'libc runtime (ctype/conversions/time)': [
        'getenv',
        'atoi',
        'atol',
        'atoll',
        'atof',
        'gmtime',
        'gmtime_r',
        'localtime',
        'localtime_r',
        'mktime',
        'strftime',
        'difftime',
        'fseeko',
        'ftello',
        'fileno',
        'isatty',
        'isalpha',
        'isdigit',
        'isalnum',
        'isspace',
        'isupper',
        'islower',
        'isprint',
        'isxdigit',
        'toupper',
        'tolower',
        'qsort',
        'bsearch',
        'arc4random',
        'arc4random_buf',
        'basename',
        'dirname',
    ],
    'setjmp/longjmp (needs CPU-context support, not a simple stub)': [
        '_setjmp',
        '_longjmp',
        'setjmp',
        'longjmp',
    ],
    'Dynamic loading': [
        'dlopen',
        'dlsym',
        'dlclose',
        'dlerror',
    ],
    'Sockets / name resolution (multiplayer + telemetry paths)': [
        'accept',
        'getsockopt',
        'getpeername',
        'getaddrinfo',
        'freeaddrinfo',
        'gethostbyname',
        'inet_addr',
        'inet_ntoa',
        'inet_ntop',
        'ioctl',
    ],
}

BUCKETS = [
    ('kernel-core (sceKernel*)', lambda n: n.startswith('sceKernel')),
    ('pthread (scePthread* / pthread_*)', lambda n: n.startswith('scePthread') or n.startswith('pthread_')),
    ('fiber/ult (sceFiber* / sceUlt*)', lambda n: n.startswith(('sceFiber', 'sceUlt', '_sceFiber', '_sceUlt'))),
    ('videoout (sceVideoOut*)', lambda n: n.startswith('sceVideoOut')),
    ('agc-gpu (sceAgc*)', lambda n: n.startswith('sceAgc')),
    ('audio (sceAudio* / sceAjm* / sceNgs2*)', lambda n: n.startswith(('sceAudio', 'sceAjm', 'sceNgs2'))),
    ('pad-input (scePad*)', lambda n: n.startswith('scePad')),
    ('system-service (sceSystemService*)', lambda n: n.startswith('sceSystemService')),
    ('user-service (sceUserService*)', lambda n: n.startswith('sceUserService')),
    ('savedata (sceSaveData*)', lambda n: n.startswith('sceSaveData')),
    ('apr/ampr (sceApr* / sceAmpr*)', lambda n: n.startswith(('sceApr', 'sceAmpr'))),
]


def main() -> None:
    catalog = load_catalog()
    registered = load_registered()

    print('<!--')
    print('Copyright (C) 2026 SharpEmu Emulator Project')
    print('SPDX-License-Identifier: GPL-2.0-or-later')
    print('-->')
    print()
    print('# HLE export coverage')
    print()
    print('Generated by `python3 scripts/report_missing_exports.py`.')
    print()
    print(f'- Catalog names: {len(catalog)}')
    print(f'- Registered exports: {len(registered)}')
    print()
    print('The catalog records every symbol ever observed, including internal')
    print('ones games never import, so the bucket totals below overstate the')
    print('real gap. The curated tier is the actionable list: each entry is a')
    print('symbol games or their middleware are known to import.')
    print()
    print('## Curated high-priority tier')
    print()
    for theme, names in HIGH_PRIORITY.items():
        missing = [n for n in names if n in catalog and n not in registered]
        if not missing:
            continue
        print(f'### {theme}')
        print()
        print('| Symbol | NID |')
        print('| --- | --- |')
        for name in missing:
            print(f'| `{name}` | `{name2nid(name)}` |')
        print()

    print('## Per-subsystem missing counts')
    print()
    print('| Bucket | Missing |')
    print('| --- | --- |')
    for title, predicate in BUCKETS:
        missing = sorted(n for n in catalog if predicate(n) and n not in registered)
        print(f'| {title} | {len(missing)} |')
    print()
    print('Regenerate the full per-bucket listings by editing the `limit` in')
    print('this script or dumping a bucket directly, e.g.:')
    print()
    print('```sh')
    print("python3 - <<'EOF'")
    print('# ... see scripts/report_missing_exports.py BUCKETS ...')
    print('EOF')
    print('```')


if __name__ == '__main__':
    main()
