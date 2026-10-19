#!/usr/bin/env python3
"""Servis gunlugunu (JSON ya da duz metin) log_scan'in bekledigi ASP.NET duz metin bicimine cevirir.

HR360_LOG_FORMAT=json ile her satir tek satirlik JSON'dur; tarama ve izin listesi (log-allowlist.txt)
"fail: Kategori[OlayNo]" bicimine gore yazildigi icin JSON satirlari bu bicime acilir. JSON olmayan
satirlar (Python servisi, eski bicim) oldugu gibi gecer.
"""
import json
import sys

LEVELS = {"Trace": "trce", "Debug": "dbug", "Information": "info", "Warning": "warn", "Error": "fail", "Critical": "crit"}

for raw in sys.stdin:
    line = raw.rstrip("\n")
    if not line.startswith("{"):
        print(line)
        continue
    try:
        e = json.loads(line)
    except ValueError:
        print(line)
        continue
    if not isinstance(e, dict) or "LogLevel" not in e:
        print(line)
        continue
    lvl = LEVELS.get(e.get("LogLevel"), "info")
    # "crit" de hata sayilir: tarama yalnizca "fail:" basligina bakar.
    head = "fail" if lvl == "crit" else lvl
    print(f"{head}: {e.get('Category', '')}[{e.get('EventId', 0)}]")
    print(f"      {e.get('Message', '')}")
    exc = e.get("Exception")
    if exc:
        for x in str(exc).splitlines()[:3]:
            print(f"      {x}")
